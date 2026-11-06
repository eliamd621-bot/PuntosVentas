USE PuntoVenta;
GO

-- 1) Todas las facturas
SELECT v.Id AS Factura,
       v.Fecha,
       c.Nombre  AS Cliente,
       ca.Nombre AS Cajero,
       v.TipoPago,
       v.SubTotal,
       v.Itbis,
       v.Total,
       v.MontoPagado,
       v.Devuelta
FROM dbo.Ventas v
JOIN dbo.Clientes c  ON c.Id  = v.ClienteId
LEFT JOIN dbo.Cajeros ca ON ca.Id = v.CajeroId
ORDER BY v.Id DESC;
GO

-- 2) Detalle de una factura (cambia el 1 por el numero de factura)
SELECT d.VentaId AS Factura,
       p.Nombre  AS Producto,
       d.Cantidad,
       d.Precio,
       d.SubTotal
FROM dbo.DetalleVentas d
JOIN dbo.Productos p ON p.Id = d.ProductoId
WHERE d.VentaId = 1;
GO

-- 3) Total vendido por cajero
SELECT ca.Nombre AS Cajero,
       COUNT(*)  AS Facturas,
       SUM(v.Total) AS TotalVendido
FROM dbo.Ventas v
LEFT JOIN dbo.Cajeros ca ON ca.Id = v.CajeroId
GROUP BY ca.Nombre
ORDER BY TotalVendido DESC;
GO

-- 4) Stock actual de productos
SELECT Id, Nombre, Precio, Stock FROM dbo.Productos ORDER BY Nombre;
GO
