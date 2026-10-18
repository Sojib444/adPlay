-- Section A answers (Q1–Q4). MySQL 8.0+. Run 01_schema.sql (and optionally 04_sample_data.sql) first.
USE adplay;

-- =====================================================================================
-- Q1. Top 3 highest-paid employees per department (no LIMIT, window function)
-- =====================================================================================
-- Exactly three rows per department. Ties on salary are broken deterministically by
-- seniority (earlier JoiningDate first) and then Id, so the result is stable between runs.
WITH ranked AS (
    SELECT e.Id,
           e.Name,
           e.DepartmentId,
           e.Salary,
           e.JoiningDate,
           ROW_NUMBER() OVER (
               PARTITION BY e.DepartmentId
               ORDER BY e.Salary DESC, e.JoiningDate ASC, e.Id ASC
           ) AS SalaryRank
    FROM Employee e
)
SELECT Id, Name, DepartmentId, Salary, JoiningDate, SalaryRank
FROM ranked
WHERE SalaryRank <= 3
ORDER BY DepartmentId, SalaryRank;

-- Variant: include ties at the boundary.
--   RANK()       -> 100, 90, 90, 80  gives ranks 1,2,2,4: everyone tied with the 3rd-ranked person is kept
--                   (can return more than 3 rows, never skips someone who earns the same as a returned row).
--   DENSE_RANK() -> top three *distinct salary levels* (1,2,2,3): can return many more rows.
WITH ranked AS (
    SELECT e.*, RANK() OVER (PARTITION BY e.DepartmentId ORDER BY e.Salary DESC) AS SalaryRank
    FROM Employee e
)
SELECT Id, Name, DepartmentId, Salary, JoiningDate, SalaryRank
FROM ranked
WHERE SalaryRank <= 3
ORDER BY DepartmentId, SalaryRank, Id;


-- =====================================================================================
-- Q2. Users with seven consecutive login days (gaps-and-islands)
-- =====================================================================================
-- 1) DISTINCT collapses several logins on the same day into one day.
-- 2) For consecutive dates, LoginDate - ROW_NUMBER() is constant, so it identifies the "island".
-- 3) Islands with >= 7 days qualify. One row per *maximal* streak: a 10-day streak is reported once
--    (StartDate..EndDate covering all 10 days) rather than as four overlapping 7-day windows.
WITH login_days AS (
    SELECT DISTINCT UserId, LoginDate            -- use DATE(LoginAt) here if the column is a DATETIME
    FROM UserLogin
),
numbered AS (
    SELECT UserId,
           LoginDate,
           ROW_NUMBER() OVER (PARTITION BY UserId ORDER BY LoginDate) AS rn
    FROM login_days
),
islands AS (
    SELECT UserId,
           MIN(LoginDate) AS StartDate,
           MAX(LoginDate) AS EndDate,
           COUNT(*)       AS StreakDays
    FROM numbered
    GROUP BY UserId, DATE_SUB(LoginDate, INTERVAL rn DAY)
)
SELECT UserId, StartDate, EndDate
FROM islands
WHERE StreakDays >= 7
ORDER BY UserId, StartDate;

-- Variant: every qualifying 7-day window (overlapping), if the business wants each window.
-- The row six positions back being exactly six days earlier means the last seven distinct days are consecutive.
WITH login_days AS (
    SELECT DISTINCT UserId, LoginDate FROM UserLogin
),
windowed AS (
    SELECT UserId,
           LoginDate,
           LAG(LoginDate, 6) OVER (PARTITION BY UserId ORDER BY LoginDate) AS SixDaysBack
    FROM login_days
)
SELECT UserId, SixDaysBack AS StartDate, LoginDate AS EndDate
FROM windowed
WHERE DATEDIFF(LoginDate, SixDaysBack) = 6
ORDER BY UserId, StartDate;


-- =====================================================================================
-- Q3. Per-order customer analytics with window functions
-- =====================================================================================
-- Deterministic order: OrderDate, then Id (unique) for orders placed at the same instant.
-- First order of a customer: PreviousAmount and DifferenceFromPrevious are NULL ("no previous order"),
-- which is more honest than 0. Wrap in COALESCE(..., 0) if the consumer needs a number.
-- The explicit ROWS frame makes the running total strictly row-by-row even if the ORDER BY had ties.
SELECT o.Id,
       o.CustomerId,
       o.OrderDate,
       o.Amount                                   AS CurrentAmount,
       LAG(o.Amount) OVER w                       AS PreviousAmount,
       o.Amount - LAG(o.Amount) OVER w            AS DifferenceFromPrevious,
       SUM(o.Amount) OVER (w ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningTotal
FROM Orders o
WINDOW w AS (PARTITION BY o.CustomerId ORDER BY o.OrderDate, o.Id)
ORDER BY o.CustomerId, o.OrderDate, o.Id;
-- IX_Orders_Customer_Date_Id (CustomerId, OrderDate, Id) delivers rows already in window order (no sort).


-- =====================================================================================
-- Q4. Index-friendly rewrite
-- =====================================================================================
-- Original problem: YEAR(o.OrderDate) = 2026 AND MONTH(o.OrderDate) = 6 wraps the column in functions,
-- so no index on OrderDate can be used (non-sargable) and every order row is evaluated.
-- Rewrite as a half-open range on the raw column; it also handles DATETIME values with time parts
-- correctly (no '23:59:59.999' edge cases). SELECT * is replaced with the columns actually needed.
SELECT o.Id,
       o.OrderDate,
       o.Amount,
       o.Status,
       c.Id   AS CustomerId,
       c.Name AS CustomerName,
       p.Id   AS ProductId,
       p.Name AS ProductName
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Products  p ON p.Id = o.ProductId
WHERE o.OrderDate >= '2026-06-01'
  AND o.OrderDate <  '2026-07-01'
  AND c.Country = 'BD'
ORDER BY o.OrderDate DESC, o.Id DESC;

-- Recommended indexes (already in 01_schema.sql):
--   Customers(Country, City)          -> equality on Country; InnoDB secondary indexes carry the PK (Id),
--                                        so this also yields the customer Ids for the join.
--   Orders(CustomerId, OrderDate, Id) -> plan when 'BD' customers are few: for each BD customer, seek the
--                                        equality column first, then range-scan June. Equality columns go
--                                        BEFORE the range column — a range ends the usable index prefix.
--   Orders(OrderDate)                 -> plan when June is the selective part: range scan June, read it
--                                        backwards for ORDER BY ... DESC (no filesort), join to Customers by PK.
--   Products(Id)                      -> primary key, already present.
-- The optimizer picks between the two plans from statistics; keep both if both access patterns are common.

-- Validate with EXPLAIN ANALYZE (actually executes the query and reports real timings/row counts):
EXPLAIN ANALYZE
SELECT o.Id, o.OrderDate, o.Amount, c.Name, p.Name
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Products  p ON p.Id = o.ProductId
WHERE o.OrderDate >= '2026-06-01' AND o.OrderDate < '2026-07-01'
  AND c.Country = 'BD'
ORDER BY o.OrderDate DESC, o.Id DESC;
-- Before: "Table scan on o" / "Filter: (year(o.OrderDate) = 2026 ...)" with rows ≈ whole table, plus "Sort".
-- After:  "Index range scan on o using IX_Orders_Date (reverse)" or "Index lookup on o using
--         IX_Orders_Customer_Date_Id", actual rows close to estimated rows, and no separate Sort step
--         in the reverse-range plan. Compare actual time= and rows= on each node before vs after,
--         run ANALYZE TABLE Orders, Customers; if estimates are far off, and test on production-sized data.
