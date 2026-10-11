-- =====================================================================================
-- OBSOLETE: this script mimics the column names B1SqlDataProvider ORIGINALLY assumed (UDFs such
-- as U_PlanMethod, a [@COBALT_ROUT] routing table, ITT1.Wastage...). The provider now reads the
-- real standard B1 schema (verified against SBODemoHO), so these stand-in tables no longer match
-- its queries. Test the provider against a real B1 company database (e.g. an SBODemo* DB) instead.
-- =====================================================================================
-- Stand-in SAP B1 tables for testing B1SqlDataProvider against a THROWAWAY database (no real B1 needed).
-- Only the columns B1SqlDataProvider reads are created. Data mirrors MockB1DataProvider; dates are
-- relative to GETDATE() like the mock's "today" anchor.
-- WARNING: drops and recreates these tables. Never run against a real B1 company DB.

DECLARE @t NVARCHAR(MAX) = N'';
SELECT @t += 'DROP TABLE ' + QUOTENAME(name) + ';' FROM sys.tables
 WHERE name IN ('OITM','OITW','OWHS','OITT','ITT1','ORDR','RDR1','OPOR','POR1','OWOR','ORSC','@COBALT_ROUT','OFCT','FCT1');
EXEC sp_executesql @t;
GO

CREATE TABLE OITM (ItemCode NVARCHAR(50) PRIMARY KEY, ItemName NVARCHAR(100), PrcrmntMtd CHAR(1), PlaningSys CHAR(1),
    LeadTime INT, MinOrder DECIMAL(19,6), MaxOrder DECIMAL(19,6), OrdrMulti DECIMAL(19,6), MinInvQty DECIMAL(19,6),
    validFor CHAR(1) DEFAULT 'Y', U_PlanMethod NVARCHAR(20), U_LotRule NVARCHAR(20), U_ReorderPoint DECIMAL(19,6),
    U_FixedLot DECIMAL(19,6), U_PeriodDays INT);
CREATE TABLE OITW (ItemCode NVARCHAR(50), WhsCode NVARCHAR(8), OnHand DECIMAL(19,6), IsCommited DECIMAL(19,6), OnOrder DECIMAL(19,6),
    MinStock DECIMAL(19,6), MaxStock DECIMAL(19,6), PRIMARY KEY (ItemCode, WhsCode));
CREATE TABLE OWHS (WhsCode NVARCHAR(8) PRIMARY KEY, WhsName NVARCHAR(100));
CREATE TABLE OITT (Code NVARCHAR(50) PRIMARY KEY);
CREATE TABLE ITT1 (Father NVARCHAR(50), ChildNum INT IDENTITY(1,1), Code NVARCHAR(50), Quantity DECIMAL(19,6), Wastage DECIMAL(19,6), Warehouse NVARCHAR(8));
CREATE TABLE ORDR (DocEntry INT PRIMARY KEY, DocStatus CHAR(1), DocDueDate DATETIME);
CREATE TABLE RDR1 (DocEntry INT, LineNum INT, ItemCode NVARCHAR(50), WhsCode NVARCHAR(8), OpenQty DECIMAL(19,6), ShipDate DATETIME, LineStatus CHAR(1));
CREATE TABLE OPOR (DocEntry INT PRIMARY KEY, DocStatus CHAR(1), DocDueDate DATETIME);
CREATE TABLE POR1 (DocEntry INT, LineNum INT, ItemCode NVARCHAR(50), WhsCode NVARCHAR(8), OpenQty DECIMAL(19,6), ShipDate DATETIME, LineStatus CHAR(1));
CREATE TABLE OWOR (DocEntry INT PRIMARY KEY, ItemCode NVARCHAR(50), Warehouse NVARCHAR(8), PlannedQty DECIMAL(19,6), CmpltQty DECIMAL(19,6),
    DueDate DATETIME, PostDate DATETIME, Status CHAR(1));
CREATE TABLE ORSC (ResCode NVARCHAR(50) PRIMARY KEY, ResName NVARCHAR(100), U_DailyCap DECIMAL(19,6), U_Efficiency DECIMAL(19,6));
CREATE TABLE [@COBALT_ROUT] (U_ItemCode NVARCHAR(50), U_Seq INT, U_WorkCenter NVARCHAR(50), U_HrsPerUnit DECIMAL(19,6), U_SetupHrs DECIMAL(19,6));
CREATE TABLE OFCT (AbsEntry INT PRIMARY KEY);
CREATE TABLE FCT1 (AbsEntry INT, ItemCode NVARCHAR(50), Warehouse NVARCHAR(8), FcstDate DATETIME, Quantity DECIMAL(19,6), U_Consumed DECIMAL(19,6));
GO

INSERT OWHS VALUES ('WH01','Main Warehouse');

-- Items: Code, Name, PrcrmntMtd, PlaningSys, LeadTime, MinOrder, MaxOrder, OrdrMulti, MinInvQty(safety), PlanMethod, LotRule, ReorderPt, FixedLot, PeriodDays
INSERT OITM (ItemCode,ItemName,PrcrmntMtd,PlaningSys,LeadTime,MinOrder,MaxOrder,OrdrMulti,MinInvQty,U_PlanMethod,U_LotRule,U_ReorderPoint,U_FixedLot,U_PeriodDays) VALUES
('BIKE-01','Mountain Bike','M','M',3, 1,0,1,5,   NULL,    NULL,    0,  0,  0),
('FRM-01','Bike Frame',    'M','M',5, 1,0,1,10,  NULL,    'FIXED', 0,  50, 0),
('WHL-01','Wheel Assembly','M','M',4, 1,0,1,20,  NULL,    'POS',   0,  0,  14),
('HBR-01','Handlebar',     'B','M',10,25,500,25,15,NULL,  'MINMAX',0,  0,  0),
('TUB-01','Steel Tubing (m)','B','M',7,100,0,1,0,'ROP',   'FIXED', 200,500,0),
('PNT-01','Paint (litre)', 'B','M',5, 20,200,20,0,'MINMAX','MINMAX',30, 0,  0),
('RIM-01','Rim',           'B','M',8, 1,0,1,30,  NULL,    NULL,    0,  0,  0),
('SPK-01','Spoke',         'B','M',6, 500,20000,500,1000,NULL,'MINMAX',0,0,0),
('TYR-01','Tyre',          'B','M',60,1,0,1,40,  NULL,    'FIXED', 0,  200,0);

INSERT OITW (ItemCode,WhsCode,OnHand,IsCommited,OnOrder) VALUES
('BIKE-01','WH01',8,10,0),('FRM-01','WH01',12,0,50),('WHL-01','WH01',30,0,0),('HBR-01','WH01',40,0,0),
('TUB-01','WH01',150,0,0),('PNT-01','WH01',15,0,0),('RIM-01','WH01',60,0,0),('SPK-01','WH01',2500,0,0),('TYR-01','WH01',25,0,0);

INSERT OITT VALUES ('BIKE-01'),('FRM-01'),('WHL-01');
-- Wastage is a percent in B1 (5 = 5%), converted to a fraction by the provider.
INSERT ITT1 (Father,Code,Quantity,Wastage,Warehouse) VALUES
('BIKE-01','FRM-01',1,0,'WH01'),('BIKE-01','WHL-01',2,0,'WH01'),('BIKE-01','HBR-01',1,0,'WH01'),
('FRM-01','TUB-01',3,5,'WH01'),('FRM-01','PNT-01',0.5,0,'WH01'),
('WHL-01','RIM-01',1,0,'WH01'),('WHL-01','SPK-01',32,2,'WH01'),('WHL-01','TYR-01',1,0,'WH01');

INSERT ORSC VALUES ('ASSY','Final Assembly',16,0.9),('WELD','Frame Welding',8,0.85),('PAINT','Paint Booth',8,1.0),('QC','Quality Control',8,1.0);
INSERT [@COBALT_ROUT] VALUES
('BIKE-01',10,'ASSY',0.75,0.5),('BIKE-01',20,'QC',0.20,0),
('FRM-01',10,'WELD',0.50,1.0),('FRM-01',20,'PAINT',0.30,0.5),
('WHL-01',10,'ASSY',0.40,0.25);

-- Sales orders (demand)
INSERT ORDR VALUES (1001,'O',DATEADD(DAY,15,CAST(GETDATE() AS DATE))),(1002,'O',DATEADD(DAY,40,CAST(GETDATE() AS DATE))),(1003,'O',DATEADD(DAY,12,CAST(GETDATE() AS DATE)));
INSERT RDR1 VALUES (1001,0,'BIKE-01','WH01',20,DATEADD(DAY,15,CAST(GETDATE() AS DATE)),'O'),
                   (1002,0,'BIKE-01','WH01',30,DATEADD(DAY,40,CAST(GETDATE() AS DATE)),'O'),
                   (1003,0,'HBR-01','WH01',10,DATEADD(DAY,12,CAST(GETDATE() AS DATE)),'O');

-- Purchase orders (supply); 4502 is already past due
INSERT OPOR VALUES (4501,'O',DATEADD(DAY,9,CAST(GETDATE() AS DATE))),(4502,'O',DATEADD(DAY,-2,CAST(GETDATE() AS DATE)));
INSERT POR1 VALUES (4501,0,'RIM-01','WH01',100,DATEADD(DAY,9,CAST(GETDATE() AS DATE)),'O'),
                   (4502,0,'TYR-01','WH01',200,DATEADD(DAY,-2,CAST(GETDATE() AS DATE)),'O');

-- Production order (supply): status R = Released
INSERT OWOR VALUES (3301,'FRM-01','WH01',50,0,DATEADD(DAY,6,CAST(GETDATE() AS DATE)),CAST(GETDATE() AS DATE),'R');

-- Forecasts
INSERT OFCT VALUES (1);
INSERT FCT1 VALUES (1,'BIKE-01','WH01',CAST(GETDATE() AS DATE),40,20),
                   (1,'BIKE-01','WH01',DATEADD(DAY,30,CAST(GETDATE() AS DATE)),40,30),
                   (1,'BIKE-01','WH01',DATEADD(DAY,60,CAST(GETDATE() AS DATE)),50,0);
GO
