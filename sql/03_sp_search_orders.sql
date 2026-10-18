-- Q5. Secure dynamic search + pagination. MySQL 8.0+.
--
-- Security model
--   * Filter VALUES never become SQL text. They are copied into session variables (@f_*) and the prepared
--     statement references the variables, so the server treats them strictly as data.
--   * Identifiers (sort column / direction) cannot be parameterised in SQL, so they come from a CASE
--     allowlist that maps a public key to a hard-coded column. Anything else raises an error.
--   * LIMIT/OFFSET are bound with "?" placeholders after being clamped.
--
-- Index-friendliness
--   * Only predicates for supplied filters are appended. Avoids the catch-all
--     "(@x IS NULL OR col = @x)" pattern, which defeats index selection.
--   * Dates use half-open ranges on the raw column; DateTo is treated as an inclusive calendar day.
--   * CustomerName is a prefix match (LIKE 'abc%') with wildcards escaped, so IX_Customers_Name is usable.
--
-- Returns two result sets: (1) TotalCount, (2) the requested page.

USE adplay;

DROP PROCEDURE IF EXISTS sp_search_orders;

DELIMITER $$

CREATE PROCEDURE sp_search_orders(
    IN p_customer_name VARCHAR(150),
    IN p_date_from     DATE,
    IN p_date_to       DATE,
    IN p_min_amount    DECIMAL(12, 2),
    IN p_max_amount    DECIMAL(12, 2),
    IN p_status        VARCHAR(20),
    IN p_country       CHAR(2),
    IN p_city          VARCHAR(100),
    IN p_sort_by       VARCHAR(30),
    IN p_sort_dir      VARCHAR(4),
    IN p_page          INT,
    IN p_page_size     INT
)
BEGIN
    DECLARE v_sort_col  VARCHAR(30);
    DECLARE v_sort_dir  VARCHAR(4);
    DECLARE v_from      VARCHAR(200) DEFAULT ' FROM Orders o JOIN Customers c ON c.Id = o.CustomerId';
    DECLARE v_where     VARCHAR(2000) DEFAULT ' WHERE 1 = 1';

    -- ---- Allowlist sort column and direction (identifiers can't be bound as parameters) ----
    SET v_sort_col = CASE LOWER(COALESCE(NULLIF(TRIM(p_sort_by), ''), 'orderdate'))
        WHEN 'orderdate'    THEN 'o.OrderDate'
        WHEN 'amount'       THEN 'o.Amount'
        WHEN 'status'       THEN 'o.Status'
        WHEN 'customername' THEN 'c.Name'
        WHEN 'city'         THEN 'c.City'
        ELSE NULL
    END;
    IF v_sort_col IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Invalid SortBy. Allowed: OrderDate, Amount, Status, CustomerName, City';
    END IF;

    SET v_sort_dir = CASE UPPER(COALESCE(NULLIF(TRIM(p_sort_dir), ''), 'DESC'))
        WHEN 'ASC'  THEN 'ASC'
        WHEN 'DESC' THEN 'DESC'
        ELSE NULL
    END;
    IF v_sort_dir IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Invalid SortDirection. Allowed: ASC, DESC';
    END IF;

    IF p_date_from IS NOT NULL AND p_date_to IS NOT NULL AND p_date_from > p_date_to THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DateFrom must be on or before DateTo';
    END IF;
    IF p_min_amount IS NOT NULL AND p_max_amount IS NOT NULL AND p_min_amount > p_max_amount THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MinAmount must be less than or equal to MaxAmount';
    END IF;

    -- ---- Pagination: clamp, never trust ----
    SET @f_page_size = LEAST(GREATEST(COALESCE(p_page_size, 20), 1), 100);
    SET @f_offset    = (GREATEST(COALESCE(p_page, 1), 1) - 1) * @f_page_size;

    -- ---- Filters: append a fixed SQL fragment, bind the value through a session variable ----
    IF p_customer_name IS NOT NULL AND TRIM(p_customer_name) <> '' THEN
        -- Escape LIKE metacharacters so user input is matched literally.
        SET @f_name = CONCAT(
            REPLACE(REPLACE(REPLACE(TRIM(p_customer_name), '\\', '\\\\'), '%', '\\%'), '_', '\\_'), '%');
        SET v_where = CONCAT(v_where, ' AND c.Name LIKE @f_name');
    END IF;
    IF p_date_from IS NOT NULL THEN
        SET @f_from = p_date_from;
        SET v_where = CONCAT(v_where, ' AND o.OrderDate >= @f_from');
    END IF;
    IF p_date_to IS NOT NULL THEN
        SET @f_to = DATE_ADD(p_date_to, INTERVAL 1 DAY);           -- inclusive day -> exclusive bound
        SET v_where = CONCAT(v_where, ' AND o.OrderDate < @f_to');
    END IF;
    IF p_min_amount IS NOT NULL THEN
        SET @f_min = p_min_amount;
        SET v_where = CONCAT(v_where, ' AND o.Amount >= @f_min');
    END IF;
    IF p_max_amount IS NOT NULL THEN
        SET @f_max = p_max_amount;
        SET v_where = CONCAT(v_where, ' AND o.Amount <= @f_max');
    END IF;
    IF p_status IS NOT NULL AND p_status <> '' THEN
        SET @f_status = p_status;
        SET v_where = CONCAT(v_where, ' AND o.Status = @f_status');
    END IF;
    IF p_country IS NOT NULL AND p_country <> '' THEN
        SET @f_country = UPPER(p_country);
        SET v_where = CONCAT(v_where, ' AND c.Country = @f_country');
    END IF;
    IF p_city IS NOT NULL AND p_city <> '' THEN
        SET @f_city = p_city;
        SET v_where = CONCAT(v_where, ' AND c.City = @f_city');
    END IF;

    -- ---- (1) Total count: same predicates, no ORDER BY, no LIMIT ----
    SET @count_sql = CONCAT('SELECT COUNT(*) AS TotalCount', v_from, v_where);
    PREPARE count_stmt FROM @count_sql;
    EXECUTE count_stmt;
    DEALLOCATE PREPARE count_stmt;

    -- ---- (2) Page: allowlisted ORDER BY + unique tie-breaker for stable paging ----
    SET @page_sql = CONCAT(
        'SELECT o.Id, o.OrderDate, o.Amount, o.Status, c.Id AS CustomerId, c.Name AS CustomerName, c.Country, c.City',
        v_from, v_where,
        ' ORDER BY ', v_sort_col, ' ', v_sort_dir, ', o.Id ', v_sort_dir,
        ' LIMIT ? OFFSET ?');
    PREPARE page_stmt FROM @page_sql;
    EXECUTE page_stmt USING @f_page_size, @f_offset;
    DEALLOCATE PREPARE page_stmt;

    -- Don't leak filter values into later statements on a pooled connection.
    SET @f_name = NULL, @f_from = NULL, @f_to = NULL, @f_min = NULL, @f_max = NULL,
        @f_status = NULL, @f_country = NULL, @f_city = NULL, @count_sql = NULL, @page_sql = NULL;
END$$

DELIMITER ;

-- Examples
CALL sp_search_orders(NULL, '2026-06-01', '2026-06-30', NULL, NULL, 'Paid', 'BD', NULL, 'Amount', 'DESC', 1, 20);
CALL sp_search_orders('Rah', NULL, NULL, 100.00, 5000.00, NULL, NULL, 'Dhaka', 'OrderDate', 'ASC', 2, 50);
-- Rejected (no injection possible: the value is not in the allowlist):
-- CALL sp_search_orders(NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'Amount; DROP TABLE Orders', 'ASC', 1, 20);
