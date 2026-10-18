-- Small, hand-checkable data set for the Section A queries.
USE adplay;

INSERT INTO Departments (Id, Name) VALUES (1, 'Engineering'), (2, 'Sales'), (3, 'Finance');

-- Engineering has a salary tie at 150000 for 2nd/3rd place and a 4th employee at 150000 too,
-- so ROW_NUMBER / RANK / DENSE_RANK give visibly different answers for Q1.
INSERT INTO Employee (Name, DepartmentId, Salary, JoiningDate) VALUES
    ('Ayesha',  1, 180000, '2022-01-01'),
    ('Rahim',   1, 150000, '2022-04-01'),
    ('Karim',   1, 150000, '2022-07-01'),
    ('Sumon',   1, 150000, '2023-01-01'),
    ('Nusrat',  1, 120000, '2022-10-01'),
    ('Tanvir',  2,  90000, '2022-02-01'),
    ('Farzana', 2, 110000, '2022-03-01');

-- User 1: 8-day streak with a duplicate login on 06-03 (one streak, 06-01..06-08).
-- User 2: 6 days, a gap, then 7 days (only the second island qualifies).
INSERT INTO UserLogin (UserId, LoginDate) VALUES
    (1, '2026-06-01'), (1, '2026-06-02'), (1, '2026-06-03'), (1, '2026-06-03'), (1, '2026-06-04'),
    (1, '2026-06-05'), (1, '2026-06-06'), (1, '2026-06-07'), (1, '2026-06-08'),
    (2, '2026-06-01'), (2, '2026-06-02'), (2, '2026-06-03'), (2, '2026-06-04'), (2, '2026-06-05'), (2, '2026-06-06'),
    (2, '2026-06-08'), (2, '2026-06-09'), (2, '2026-06-10'), (2, '2026-06-11'), (2, '2026-06-12'),
    (2, '2026-06-13'), (2, '2026-06-14');

INSERT INTO Customers (Id, Name, Country, City) VALUES
    (1, 'Rahman Traders', 'BD', 'Dhaka'), (2, 'Chowdhury Ltd', 'BD', 'Chattogram'), (3, 'Acme Inc', 'US', 'Austin');

INSERT INTO Categories (Id, Name) VALUES (1, 'Phones');
INSERT INTO Brands (Id, Name) VALUES (1, 'Acme');
INSERT INTO Products (Id, Name, Price, CategoryId, BrandId, CreatedAt) VALUES
    (1, 'Phone X', 500.00, 1, 1, '2026-01-01'), (2, 'Phone Y', 750.00, 1, 1, '2026-02-01');

-- Two orders for customer 1 share the same timestamp: Id breaks the tie in Q3.
INSERT INTO Orders (CustomerId, ProductId, OrderDate, Amount, Status) VALUES
    (1, 1, '2026-05-30 10:00:00', 500.00, 'Paid'),
    (1, 2, '2026-06-05 09:00:00', 750.00, 'Paid'),
    (1, 1, '2026-06-05 09:00:00', 500.00, 'Pending'),
    (2, 2, '2026-06-30 23:59:59', 750.00, 'Paid'),
    (3, 1, '2026-06-15 12:00:00', 500.00, 'Paid');
