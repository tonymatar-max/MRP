-- Cobalt MRP run-result tables (mirror CobaltMrpDbContext). Run against the CobaltMrp database.
IF OBJECT_ID('dbo.MrpRun') IS NULL
CREATE TABLE dbo.MrpRun (
    Id            UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    RunAt         DATETIMEOFFSET   NOT NULL,
    RunBy         NVARCHAR(100)    NOT NULL,
    HorizonStart  DATE             NOT NULL,
    HorizonEnd    DATE             NOT NULL,
    BucketDays    INT              NOT NULL,
    Notes         NVARCHAR(1000)   NULL
);

IF OBJECT_ID('dbo.PlannedOrder') IS NULL
CREATE TABLE dbo.PlannedOrder (
    Id            UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    MrpRunId      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.MrpRun(Id) ON DELETE CASCADE,
    ItemCode      NVARCHAR(50)     NOT NULL,
    WarehouseCode NVARCHAR(8)      NOT NULL,
    Type          INT              NOT NULL,   -- PlannedOrderType
    Quantity      DECIMAL(19,6)    NOT NULL,
    DueDate       DATE             NOT NULL,
    ReleaseDate   DATE             NOT NULL,
    LowLevelCode  INT              NOT NULL,
    Firmed        BIT              NOT NULL,
    B1DocEntry    NVARCHAR(50)     NULL
);
CREATE INDEX IX_PlannedOrder_MrpRunId ON dbo.PlannedOrder(MrpRunId);

IF OBJECT_ID('dbo.PeggingLink') IS NULL
CREATE TABLE dbo.PeggingLink (
    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    MrpRunId        UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.MrpRun(Id) ON DELETE CASCADE,
    PlannedOrderId  UNIQUEIDENTIFIER NOT NULL,   -- no FK (avoids multiple cascade paths)
    DemandSource    INT              NOT NULL,
    Quantity        DECIMAL(19,6)    NOT NULL,
    DemandDueDate   DATE             NOT NULL,
    DemandDocKey    NVARCHAR(100)    NULL,
    ParentItemCode  NVARCHAR(50)     NULL
);
CREATE INDEX IX_PeggingLink_MrpRunId ON dbo.PeggingLink(MrpRunId);
CREATE INDEX IX_PeggingLink_PlannedOrderId ON dbo.PeggingLink(PlannedOrderId);

IF OBJECT_ID('dbo.ExceptionMessage') IS NULL
CREATE TABLE dbo.ExceptionMessage (
    Id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    MrpRunId      UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.MrpRun(Id) ON DELETE CASCADE,
    Type          INT              NOT NULL,   -- ExceptionType
    ItemCode      NVARCHAR(50)     NOT NULL,
    WarehouseCode NVARCHAR(8)      NULL,
    Message       NVARCHAR(500)    NOT NULL,
    RelatedDate   DATE             NULL,
    SourceDocKey  NVARCHAR(100)    NULL,
    Quantity      DECIMAL(19,6)    NOT NULL
);
CREATE INDEX IX_ExceptionMessage_MrpRunId ON dbo.ExceptionMessage(MrpRunId);

IF OBJECT_ID('dbo.CapacityLoad') IS NULL
CREATE TABLE dbo.CapacityLoad (
    Id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    MrpRunId       UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.MrpRun(Id) ON DELETE CASCADE,
    WorkCenterCode NVARCHAR(50)     NOT NULL,
    BucketStart    DATE             NOT NULL,
    RequiredHours  DECIMAL(19,6)    NOT NULL,
    AvailableHours DECIMAL(19,6)    NOT NULL
);
CREATE INDEX IX_CapacityLoad_MrpRunId ON dbo.CapacityLoad(MrpRunId);

-- Cobalt-managed demand forecasts (Forecasts screen). Same shape as EF's ForecastRow.
IF OBJECT_ID('dbo.Forecast') IS NULL
BEGIN
    CREATE TABLE dbo.Forecast (
        Id            NVARCHAR(32)   NOT NULL PRIMARY KEY,
        ItemCode      NVARCHAR(50)   NOT NULL,
        WarehouseCode NVARCHAR(8)    NOT NULL,
        PeriodStart   DATE           NOT NULL,
        Quantity      DECIMAL(19,6)  NOT NULL,
        Consumed      DECIMAL(19,6)  NOT NULL
    );
    CREATE INDEX IX_Forecast_Item ON dbo.Forecast (ItemCode, WarehouseCode, PeriodStart);
END

-- v3: preferred vendor on planned orders, and the planning calendar (same shape as EF's CalendarEvent).
IF COL_LENGTH('dbo.PlannedOrder', 'VendorCode') IS NULL
    ALTER TABLE dbo.PlannedOrder ADD VendorCode NVARCHAR(50) NULL;
GO
IF OBJECT_ID('dbo.CalendarEvent') IS NULL
BEGIN
    CREATE TABLE dbo.CalendarEvent (
        Id            UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        MrpRunId      UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT FK_CalendarEvent_MrpRun REFERENCES dbo.MrpRun(Id) ON DELETE CASCADE,
        [Date]        DATE           NOT NULL,
        Kind          INT            NOT NULL,
        ItemCode      NVARCHAR(50)   NOT NULL,
        WarehouseCode NVARCHAR(8)    NULL,
        Quantity      DECIMAL(19,6)  NOT NULL,
        Label         NVARCHAR(500)  NOT NULL,
        Ref           NVARCHAR(100)  NULL,
        Severity      NVARCHAR(10)   NOT NULL
    );
    CREATE INDEX IX_CalendarEvent_MrpRunId_Date ON dbo.CalendarEvent (MrpRunId, [Date]);
END

-- v4: wizard criteria on runs, lead-time/vendor provenance on planned orders, master-data overrides.
IF COL_LENGTH('dbo.MrpRun', 'CriteriaJson') IS NULL ALTER TABLE dbo.MrpRun ADD CriteriaJson NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.PlannedOrder', 'LeadTimeDays') IS NULL ALTER TABLE dbo.PlannedOrder ADD LeadTimeDays INT NOT NULL CONSTRAINT DF_PlannedOrder_LeadTimeDays DEFAULT 0;
IF COL_LENGTH('dbo.PlannedOrder', 'LeadTimeSource') IS NULL ALTER TABLE dbo.PlannedOrder ADD LeadTimeSource NVARCHAR(20) NULL;
IF COL_LENGTH('dbo.PlannedOrder', 'VendorSource') IS NULL ALTER TABLE dbo.PlannedOrder ADD VendorSource NVARCHAR(30) NULL;
IF COL_LENGTH('dbo.PlannedOrder', 'FromWarehouse') IS NULL ALTER TABLE dbo.PlannedOrder ADD FromWarehouse NVARCHAR(8) NULL;
GO
IF OBJECT_ID('dbo.ItemPlanning') IS NULL
    CREATE TABLE dbo.ItemPlanning (
        ItemCode NVARCHAR(50) NOT NULL PRIMARY KEY, PlanningMethod INT NULL, LotSizing INT NULL, LeadTimeDays INT NULL,
        MinOrderQty DECIMAL(19,6) NULL, OrderMultiple DECIMAL(19,6) NULL, SafetyStock DECIMAL(19,6) NULL,
        ReorderPoint DECIMAL(19,6) NULL, MaxStockLevel DECIMAL(19,6) NULL, FixedLotSize DECIMAL(19,6) NULL,
        PeriodOfSupplyDays INT NULL, Supplier NVARCHAR(50) NULL, Notes NVARCHAR(1000) NOT NULL,
        UpdatedBy NVARCHAR(100) NOT NULL, UpdatedAt DATETIMEOFFSET NOT NULL);
GO
IF OBJECT_ID('dbo.SupplierPlanning') IS NULL
    CREATE TABLE dbo.SupplierPlanning (
        CardCode NVARCHAR(50) NOT NULL PRIMARY KEY, LeadTimeDays INT NULL, Notes NVARCHAR(1000) NOT NULL,
        UpdatedBy NVARCHAR(100) NOT NULL, UpdatedAt DATETIMEOFFSET NOT NULL);

-- v5: exceptions can be executed (ids, links to the order/document, dates, audit trail).
IF COL_LENGTH('dbo.ExceptionMessage', 'ExceptionId') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ExceptionId UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ExceptionMessage_ExceptionId DEFAULT NEWID();
IF COL_LENGTH('dbo.ExceptionMessage', 'SourceLine') IS NULL ALTER TABLE dbo.ExceptionMessage ADD SourceLine INT NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'PlannedOrderId') IS NULL ALTER TABLE dbo.ExceptionMessage ADD PlannedOrderId UNIQUEIDENTIFIER NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'CurrentDate') IS NULL ALTER TABLE dbo.ExceptionMessage ADD CurrentDate DATE NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ProposedDate') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ProposedDate DATE NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionStatus') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionStatus NVARCHAR(20) NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionBy') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionBy NVARCHAR(100) NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionAt') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionAt DATETIMEOFFSET NULL;
IF COL_LENGTH('dbo.ExceptionMessage', 'ActionNote') IS NULL ALTER TABLE dbo.ExceptionMessage ADD ActionNote NVARCHAR(500) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExceptionMessage_ExceptionId') CREATE INDEX IX_ExceptionMessage_ExceptionId ON dbo.ExceptionMessage (ExceptionId);
