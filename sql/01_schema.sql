-- MySQL 8.0+ schema used by the written-test answers and the AdPlay.Api project.
-- utf8mb4 everywhere; InnoDB for row-level locking and FK enforcement.

CREATE DATABASE IF NOT EXISTS adplay CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
USE adplay;

CREATE TABLE IF NOT EXISTS Departments (
    Id   INT AUTO_INCREMENT PRIMARY KEY,
    Name VARCHAR(100) NOT NULL
) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS Employee (
    Id           INT AUTO_INCREMENT PRIMARY KEY,
    Name         VARCHAR(150)   NOT NULL,
    DepartmentId INT            NOT NULL,
    Salary       DECIMAL(12, 2) NOT NULL,
    JoiningDate  DATETIME(6)    NOT NULL,
    CONSTRAINT FK_Employee_Department FOREIGN KEY (DepartmentId) REFERENCES Departments (Id),
    -- Q1/Q13: lets the window function read each department already ordered by salary.
    INDEX IX_Employee_Department_Salary (DepartmentId, Salary)
) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS UserLogin (
    Id        BIGINT AUTO_INCREMENT PRIMARY KEY,
    UserId    INT  NOT NULL,
    LoginDate DATE NOT NULL,
    -- Q2: DISTINCT (UserId, LoginDate) and the per-user ordering are both served by this index.
    INDEX IX_UserLogin_User_Date (UserId, LoginDate)
) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS Customers (
    Id      INT AUTO_INCREMENT PRIMARY KEY,
    Name    VARCHAR(150) NOT NULL,
    Country CHAR(2)      NOT NULL,
    City    VARCHAR(100) NOT NULL,
    INDEX IX_Customers_Country_City (Country, City),
    INDEX IX_Customers_Name (Name)
) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS Categories (Id INT AUTO_INCREMENT PRIMARY KEY, Name VARCHAR(100) NOT NULL) ENGINE = InnoDB;
CREATE TABLE IF NOT EXISTS Brands     (Id INT AUTO_INCREMENT PRIMARY KEY, Name VARCHAR(100) NOT NULL) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS Products (
    Id          INT AUTO_INCREMENT PRIMARY KEY,
    Name        VARCHAR(200)   NOT NULL,
    Description VARCHAR(2000)  NULL,
    Price       DECIMAL(12, 2) NOT NULL,
    CategoryId  INT            NOT NULL,
    BrandId     INT            NOT NULL,
    IsActive    TINYINT(1)     NOT NULL DEFAULT 1,
    CreatedAt   DATETIME(6)    NOT NULL,
    CONSTRAINT FK_Products_Category FOREIGN KEY (CategoryId) REFERENCES Categories (Id),
    CONSTRAINT FK_Products_Brand    FOREIGN KEY (BrandId)    REFERENCES Brands (Id),
    INDEX IX_Products_Active_Category_Price (IsActive, CategoryId, Price),
    INDEX IX_Products_Active_Brand_Price    (IsActive, BrandId, Price),
    INDEX IX_Products_Active_Created_Id     (IsActive, CreatedAt, Id),   -- Q15 keyset pagination
    INDEX IX_Products_Name (Name),
    FULLTEXT INDEX FTX_Products_Name_Description (Name, Description)    -- Q14/Q15 keyword search at scale
) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS Orders (
    Id         BIGINT AUTO_INCREMENT PRIMARY KEY,
    CustomerId INT            NOT NULL,
    ProductId  INT            NULL,
    OrderDate  DATETIME(6)    NOT NULL,
    Amount     DECIMAL(12, 2) NOT NULL,
    Status     VARCHAR(20)    NOT NULL DEFAULT 'Pending',
    CONSTRAINT FK_Orders_Customer FOREIGN KEY (CustomerId) REFERENCES Customers (Id),
    CONSTRAINT FK_Orders_Product  FOREIGN KEY (ProductId)  REFERENCES Products (Id),
    INDEX IX_Orders_Customer_Date_Id (CustomerId, OrderDate, Id),  -- Q3 windows, Q4 plan B, Q5 customer filters
    INDEX IX_Orders_Date (OrderDate),                              -- Q4 plan A, Q5 date-range filters
    INDEX IX_Orders_Status_Date (Status, OrderDate)                -- Q5 status + date
) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS Users (
    Id           INT AUTO_INCREMENT PRIMARY KEY,
    Mobile       VARCHAR(20) NOT NULL,
    IsSubscribed TINYINT(1)  NOT NULL DEFAULT 0,
    SubscribedAt DATETIME(6) NULL,
    UNIQUE INDEX UX_Users_Mobile (Mobile)
) ENGINE = InnoDB;

-- Q6: one subscription per user, enforced by the database regardless of application code.
CREATE TABLE IF NOT EXISTS Subscriptions (
    Id        BIGINT AUTO_INCREMENT PRIMARY KEY,
    UserId    INT         NOT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UNIQUE INDEX UX_Subscriptions_User (UserId),
    CONSTRAINT FK_Subscriptions_User FOREIGN KEY (UserId) REFERENCES Users (Id)
) ENGINE = InnoDB;

-- Q7
CREATE TABLE IF NOT EXISTS Payments (
    Id                CHAR(36)       NOT NULL PRIMARY KEY,
    MerchantId        VARCHAR(64)    NOT NULL,
    IdempotencyKey    VARCHAR(64)    NOT NULL,
    RequestHash       CHAR(64)       NOT NULL,
    OrderId           VARCHAR(64)    NOT NULL,
    ActiveOrderId     VARCHAR(64)    NULL,      -- = OrderId while live, NULL once Failed
    Amount            DECIMAL(12, 2) NOT NULL,
    Currency          CHAR(3)        NOT NULL,
    Status            VARCHAR(20)    NOT NULL,  -- Pending | Processing | Succeeded | Failed | Unknown
    ProviderReference VARCHAR(100)   NULL,
    FailureReason     VARCHAR(200)   NULL,
    Attempts          INT            NOT NULL DEFAULT 0,
    CreatedAt         DATETIME(6)    NOT NULL,
    UpdatedAt         DATETIME(6)    NOT NULL,
    UNIQUE INDEX UX_Payments_Merchant_IdempotencyKey (MerchantId, IdempotencyKey),
    UNIQUE INDEX UX_Payments_Merchant_ActiveOrder (MerchantId, ActiveOrderId), -- NULLs don't collide
    INDEX IX_Payments_ProviderReference (ProviderReference),
    INDEX IX_Payments_Status_UpdatedAt (Status, UpdatedAt)
) ENGINE = InnoDB;

CREATE TABLE IF NOT EXISTS WebhookEvents (
    Id                BIGINT AUTO_INCREMENT PRIMARY KEY,
    Provider          VARCHAR(32)  NOT NULL,
    ProviderEventId   VARCHAR(100) NOT NULL,
    EventType         VARCHAR(50)  NOT NULL,
    ProviderReference VARCHAR(100) NULL,
    ReceivedAt        DATETIME(6)  NOT NULL,
    ProcessedAt       DATETIME(6)  NULL,
    UNIQUE INDEX UX_WebhookEvents_Provider_Event (Provider, ProviderEventId)
) ENGINE = InnoDB;
