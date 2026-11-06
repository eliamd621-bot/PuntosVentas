using System.Data;
using System.Drawing.Printing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace PuntoVenta;

// Datos de quien inicio sesion
class Sesion
{
    public int Id;
    public string Usuario;
    public string Nombre;
    public string Rol;       // Admin, Cajero o Cliente
    public int? ClienteId;   // solo para el rol Cliente
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            Db.Init();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "No se pudo conectar a SQL Server.\n\n" + ex.Message +
                "\n\nRevisa la constante SERVIDOR en la clase Db (Program.cs).",
                "Error de base de datos", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var login = new FormLogin();
        if (login.ShowDialog() != DialogResult.OK) return;

        Sesion s = login.Resultado;
        Form principal;
        if (s.Rol == "Admin") principal = new FormAdmin(s);
        else if (s.Rol == "Cajero") principal = new FormVenta(s.Id, s.Nombre);
        else principal = new FormCliente(s);

        Application.Run(principal);
    }
}

// ---------------------------------------------------------------
// BASE DE DATOS (SQL Server). Crea la base "PuntoVenta" sola.
// ---------------------------------------------------------------
static class Db
{
    // Si tu instancia se llama distinto, cambia esta linea.
    // Ejemplos:  (localdb)\MSSQLLocalDB    .\SQLEXPRESS    localhost
    const string SERVIDOR = @".\SQLEXPRESS";
    const string BASE = "PuntoVenta";

    static string Cadena(string bd) =>
        $"Server={SERVIDOR};Database={bd};Integrated Security=True;TrustServerCertificate=True;Connect Timeout=30";

    public static SqlConnection Abrir()
    {
        var c = new SqlConnection(Cadena(BASE));
        c.Open();
        return c;
    }

    static void Ejecutar(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    public static void Init()
    {
        // 1) Crear la base de datos si no existe
        using (var m = new SqlConnection(Cadena("master")))
        {
            m.Open();
            Ejecutar(m, $"IF DB_ID('{BASE}') IS NULL CREATE DATABASE [{BASE}];");
        }

        // 2) Crear tablas
        using (var c = Abrir())
        {
            // La tabla Cajeros guarda TODOS los usuarios (Admin, Cajero, Cliente)
            Ejecutar(c, @"
IF OBJECT_ID('dbo.Cajeros','U') IS NULL
CREATE TABLE dbo.Cajeros(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Usuario NVARCHAR(50) NOT NULL UNIQUE,
    Nombre NVARCHAR(100) NOT NULL,
    Salt NVARCHAR(100) NOT NULL,
    Hash NVARCHAR(200) NOT NULL,
    Rol NVARCHAR(20) NOT NULL DEFAULT 'Cajero',
    ClienteId INT NULL);");

            // Migracion para bases creadas con la version anterior
            Ejecutar(c, "IF COL_LENGTH('dbo.Cajeros','Rol') IS NULL ALTER TABLE dbo.Cajeros ADD Rol NVARCHAR(20) NOT NULL DEFAULT 'Cajero';");
            Ejecutar(c, "IF COL_LENGTH('dbo.Cajeros','ClienteId') IS NULL ALTER TABLE dbo.Cajeros ADD ClienteId INT NULL;");

            Ejecutar(c, @"
IF OBJECT_ID('dbo.Clientes','U') IS NULL
CREATE TABLE dbo.Clientes(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Nombre NVARCHAR(100) NOT NULL);");

            Ejecutar(c, @"
IF OBJECT_ID('dbo.Productos','U') IS NULL
CREATE TABLE dbo.Productos(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Nombre NVARCHAR(100) NOT NULL,
    Precio DECIMAL(18,2) NOT NULL,
    Stock INT NOT NULL);");

            Ejecutar(c, @"
IF OBJECT_ID('dbo.Ventas','U') IS NULL
CREATE TABLE dbo.Ventas(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Fecha DATETIME NOT NULL,
    ClienteId INT NOT NULL REFERENCES dbo.Clientes(Id),
    CajeroId INT NULL REFERENCES dbo.Cajeros(Id),
    TipoPago NVARCHAR(30) NOT NULL,
    SubTotal DECIMAL(18,2) NOT NULL,
    Itbis DECIMAL(18,2) NOT NULL,
    Total DECIMAL(18,2) NOT NULL,
    MontoPagado DECIMAL(18,2) NOT NULL,
    Devuelta DECIMAL(18,2) NOT NULL);");

            Ejecutar(c, @"
IF OBJECT_ID('dbo.DetalleVentas','U') IS NULL
CREATE TABLE dbo.DetalleVentas(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    VentaId INT NOT NULL REFERENCES dbo.Ventas(Id),
    ProductoId INT NOT NULL REFERENCES dbo.Productos(Id),
    Cantidad INT NOT NULL,
    Precio DECIMAL(18,2) NOT NULL,
    SubTotal DECIMAL(18,2) NOT NULL);");

            // 3) Datos de ejemplo
            Ejecutar(c, @"
IF NOT EXISTS (SELECT 1 FROM dbo.Clientes)
INSERT INTO dbo.Clientes(Nombre) VALUES
 (N'German Gibbs'),(N'Monica Vitti'),(N'Fanny Alexander'),(N'Ingmar Bergman'),(N'Samuel Beckett');");

            Ejecutar(c, @"
IF NOT EXISTS (SELECT 1 FROM dbo.Productos)
INSERT INTO dbo.Productos(Nombre,Precio,Stock) VALUES
 (N'Laptop',35000,20),(N'Mouse',450,100),(N'Teclado',1200,50),
 (N'Monitor 24""',9500,15),(N'Audifonos',1800,40),(N'Memoria USB 64GB',600,80);");
        }

        // 4) Administrador por defecto: usuario "admin" / contrasena "admin123"
        Auth.AsegurarAdmin();
    }
}

// ---------------------------------------------------------------
// ACCESO A DATOS (ayudas cortas)
// ---------------------------------------------------------------
static class Datos
{
    static SqlCommand Crear(SqlConnection c, string sql, (string nombre, object valor)[] ps)
    {
        var cmd = new SqlCommand(sql, c);
        foreach (var p in ps) cmd.Parameters.AddWithValue(p.nombre, p.valor ?? DBNull.Value);
        return cmd;
    }

    public static DataTable Tabla(string sql, params (string nombre, object valor)[] ps)
    {
        using var c = Db.Abrir();
        using var cmd = Crear(c, sql, ps);
        var dt = new DataTable();
        using var da = new SqlDataAdapter(cmd);
        da.Fill(dt);
        return dt;
    }

    public static int Ejecutar(string sql, params (string nombre, object valor)[] ps)
    {
        using var c = Db.Abrir();
        using var cmd = Crear(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    public static object Escalar(string sql, params (string nombre, object valor)[] ps)
    {
        using var c = Db.Abrir();
        using var cmd = Crear(c, sql, ps);
        return cmd.ExecuteScalar();
    }
}

static class UI
{
    public static DataGridView Grilla(int x, int y, int w, int h)
    {
        var g = new DataGridView
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            RowHeadersVisible = false
        };
        g.DataBindingComplete += (s, e) =>
        {
            foreach (DataGridViewColumn col in g.Columns)
            {
                if (col.ValueType == typeof(decimal) || col.ValueType == typeof(double))
                {
                    col.DefaultCellStyle.Format = "N2";
                    col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                }
            }
        };
        return g;
    }
}

// ---------------------------------------------------------------
// AUTENTICACION Y ROLES
// ---------------------------------------------------------------
static class Auth
{
    static string Hash(string pass, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(pass, salt, 100000, HashAlgorithmName.SHA256, 32));

    static void Insertar(SqlConnection c, string usuario, string nombre, string pass, string rol, int? clienteId)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var cmd = new SqlCommand(
            "INSERT INTO Cajeros(Usuario,Nombre,Salt,Hash,Rol,ClienteId) VALUES(@u,@n,@s,@h,@r,@ci)", c);
        cmd.Parameters.AddWithValue("@u", usuario);
        cmd.Parameters.AddWithValue("@n", nombre);
        cmd.Parameters.AddWithValue("@s", Convert.ToBase64String(salt));
        cmd.Parameters.AddWithValue("@h", Hash(pass, salt));
        cmd.Parameters.AddWithValue("@r", rol);
        cmd.Parameters.AddWithValue("@ci", (object)clienteId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // Crea el admin por defecto si no existe ninguno
    public static void AsegurarAdmin()
    {
        using var c = Db.Abrir();

        using (var chk = new SqlCommand("SELECT COUNT(*) FROM Cajeros WHERE Rol = 'Admin'", c))
            if (Convert.ToInt32(chk.ExecuteScalar()) > 0) return;

        using (var ex = new SqlCommand("SELECT COUNT(*) FROM Cajeros WHERE Usuario = 'admin'", c))
        {
            if (Convert.ToInt32(ex.ExecuteScalar()) > 0)
            {
                using var up = new SqlCommand("UPDATE Cajeros SET Rol = 'Admin' WHERE Usuario = 'admin'", c);
                up.ExecuteNonQuery();
                return;
            }
        }

        Insertar(c, "admin", "Administrador", "admin123", "Admin", null);
    }

    // Registro publico: solo se puede ser Cliente o Cajero
    public static bool Registrar(string usuario, string nombre, string pass, string rol, out string error)
    {
        error = null;
        if (rol != "Cliente" && rol != "Cajero")
        {
            error = "Rol invalido.";
            return false;
        }

        using var c = Db.Abrir();

        using (var chk = new SqlCommand("SELECT COUNT(*) FROM Cajeros WHERE Usuario = @u", c))
        {
            chk.Parameters.AddWithValue("@u", usuario);
            if (Convert.ToInt32(chk.ExecuteScalar()) > 0)
            {
                error = "Ese usuario ya existe.";
                return false;
            }
        }

        int? clienteId = null;
        if (rol == "Cliente")
        {
            using var cc = new SqlCommand("INSERT INTO Clientes(Nombre) OUTPUT INSERTED.Id VALUES(@n)", c);
            cc.Parameters.AddWithValue("@n", nombre);
            clienteId = Convert.ToInt32(cc.ExecuteScalar());
        }

        Insertar(c, usuario, nombre, pass, rol, clienteId);
        return true;
    }

    public static Sesion Login(string usuario, string pass)
    {
        using var c = Db.Abrir();
        using var cmd = new SqlCommand(
            "SELECT Id, Usuario, Nombre, Salt, Hash, Rol, ClienteId FROM Cajeros WHERE Usuario = @u", c);
        cmd.Parameters.AddWithValue("@u", usuario);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        byte[] salt = Convert.FromBase64String(r.GetString(3));
        if (Hash(pass, salt) != r.GetString(4)) return null;

        return new Sesion
        {
            Id = r.GetInt32(0),
            Usuario = r.GetString(1),
            Nombre = r.GetString(2),
            Rol = r.GetString(5),
            ClienteId = r.IsDBNull(6) ? null : r.GetInt32(6)
        };
    }
}

// ---------------------------------------------------------------
// FORMULARIO LOGIN / REGISTRO
// ---------------------------------------------------------------
class FormLogin : Form
{
    public Sesion Resultado;

    Label lblTitulo, lblNombre, lblRol, lblPass, lblPass2;
    TextBox txtUsuario, txtNombre, txtPass, txtPass2;
    ComboBox cboRol;
    Button btnAccion;
    LinkLabel lnkModo;
    bool modoRegistro = false;

    public FormLogin()
    {
        Text = "Acceso al sistema";
        ClientSize = new Size(380, 300);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(220, 232, 224);
        Font = new Font("Consolas", 11);

        lblTitulo = new Label { Location = new Point(20, 15), AutoSize = true, Font = new Font("Consolas", 14, FontStyle.Bold) };
        Controls.Add(lblTitulo);

        Controls.Add(new Label { Text = "Usuario:", Location = new Point(20, 60), AutoSize = true });
        txtUsuario = new TextBox { Location = new Point(20, 85), Width = 340 };
        Controls.Add(txtUsuario);

        lblNombre = new Label { Text = "Nombre completo:", Location = new Point(20, 120), AutoSize = true };
        txtNombre = new TextBox { Location = new Point(20, 145), Width = 340 };
        Controls.Add(lblNombre);
        Controls.Add(txtNombre);

        lblRol = new Label { Text = "Registrarme como:", Location = new Point(20, 180), AutoSize = true };
        cboRol = new ComboBox { Location = new Point(20, 205), Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
        cboRol.Items.AddRange(new object[] { "Cliente", "Cajero" });
        cboRol.SelectedIndex = 0;
        Controls.Add(lblRol);
        Controls.Add(cboRol);

        lblPass = new Label { Text = "Contrasena:", AutoSize = true };
        txtPass = new TextBox { Width = 340, UseSystemPasswordChar = true };
        Controls.Add(lblPass);
        Controls.Add(txtPass);

        lblPass2 = new Label { Text = "Confirmar contrasena:", AutoSize = true };
        txtPass2 = new TextBox { Width = 340, UseSystemPasswordChar = true };
        Controls.Add(lblPass2);
        Controls.Add(txtPass2);

        btnAccion = new Button
        {
            Size = new Size(340, 42),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnAccion.Click += (s, e) => Accion();
        Controls.Add(btnAccion);
        AcceptButton = btnAccion;

        lnkModo = new LinkLabel { AutoSize = true };
        lnkModo.Click += (s, e) => { modoRegistro = !modoRegistro; AplicarModo(); };
        Controls.Add(lnkModo);

        AplicarModo();
    }

    void AplicarModo()
    {
        lblTitulo.Text = modoRegistro ? "Crear cuenta" : "Iniciar sesion";
        btnAccion.Text = modoRegistro ? "Registrarme" : "Entrar";
        lnkModo.Text = modoRegistro ? "Ya tengo cuenta: iniciar sesion" : "No tengo cuenta: registrarme";

        lblNombre.Visible = txtNombre.Visible = modoRegistro;
        lblRol.Visible = cboRol.Visible = modoRegistro;
        lblPass2.Visible = txtPass2.Visible = modoRegistro;

        if (modoRegistro)
        {
            lblPass.Location = new Point(20, 240);
            txtPass.Location = new Point(20, 265);
            lblPass2.Location = new Point(20, 300);
            txtPass2.Location = new Point(20, 325);
            btnAccion.Location = new Point(20, 365);
            lnkModo.Location = new Point(20, 420);
            ClientSize = new Size(380, 455);
        }
        else
        {
            lblPass.Location = new Point(20, 120);
            txtPass.Location = new Point(20, 145);
            btnAccion.Location = new Point(20, 195);
            lnkModo.Location = new Point(20, 250);
            ClientSize = new Size(380, 300);
        }
        txtPass.Clear();
        txtPass2.Clear();
    }

    void Accion()
    {
        string usuario = txtUsuario.Text.Trim();
        string pass = txtPass.Text;

        if (usuario == "" || pass == "")
        {
            MessageBox.Show("Completa usuario y contrasena.");
            return;
        }

        if (modoRegistro)
        {
            string nombre = txtNombre.Text.Trim();
            if (nombre == "")
            {
                MessageBox.Show("Escribe tu nombre completo.");
                return;
            }
            if (pass.Length < 4)
            {
                MessageBox.Show("La contrasena debe tener al menos 4 caracteres.");
                return;
            }
            if (pass != txtPass2.Text)
            {
                MessageBox.Show("Las contrasenas no coinciden.");
                return;
            }
            if (!Auth.Registrar(usuario, nombre, pass, cboRol.SelectedItem.ToString(), out string error))
            {
                MessageBox.Show(error);
                return;
            }
            MessageBox.Show("Cuenta creada. Ahora inicia sesion.");
            modoRegistro = false;
            AplicarModo();
            return;
        }

        var s = Auth.Login(usuario, pass);
        if (s == null)
        {
            MessageBox.Show("Usuario o contrasena incorrectos.");
            return;
        }
        Resultado = s;
        DialogResult = DialogResult.OK;
        Close();
    }
}

// ---------------------------------------------------------------
// PANEL DEL ADMINISTRADOR
// ---------------------------------------------------------------
class FormAdmin : Form
{
    readonly Sesion ses;
    TabControl tabs;

    // Facturas
    ComboBox cboFiltro;
    DataGridView gFacturas, gDetalle;
    Label lblResumen;

    // Productos
    DataGridView gProductos;
    TextBox txtPNombre, txtPPrecio, txtPStock;
    int productoSel = 0;

    // Usuarios
    DataGridView gUsuarios;
    ComboBox cboRolUsuario;

    public FormAdmin(Sesion ses)
    {
        this.ses = ses;
        Text = "Administrador - " + ses.Nombre;
        ClientSize = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        Controls.Add(new Label
        {
            Text = "Panel de administración",
            Location = new Point(12, 10),
            AutoSize = true,
            Font = new Font("Segoe UI", 14, FontStyle.Bold)
        });

        var btnCaja = new Button { Text = "Abrir caja (vender)", Location = new Point(780, 8), Size = new Size(200, 32) };
        btnCaja.Click += (s, e) =>
        {
            using var f = new FormVenta(ses.Id, ses.Nombre);
            f.ShowDialog(this);
            CargarFacturas();
            CargarProductos();
        };
        Controls.Add(btnCaja);

        tabs = new TabControl { Location = new Point(0, 50), Size = new Size(1000, 570) };
        tabs.TabPages.Add(CrearTabFacturas());
        tabs.TabPages.Add(CrearTabProductos());
        tabs.TabPages.Add(CrearTabUsuarios());
        Controls.Add(tabs);

        CargarFacturas();
        CargarProductos();
        CargarUsuarios();
    }

    static void Msg(string t) => MessageBox.Show(t);

    // ===================== FACTURAS =====================
    TabPage CrearTabFacturas()
    {
        var tp = new TabPage("Facturas");

        tp.Controls.Add(new Label { Text = "Tipo de pago:", Location = new Point(10, 15), AutoSize = true });
        cboFiltro = new ComboBox { Location = new Point(110, 11), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        cboFiltro.Items.AddRange(new object[] { "Todos", "Efectivo", "Tarjeta", "Transferencia" });
        cboFiltro.SelectedIndex = 0;
        cboFiltro.SelectedIndexChanged += (s, e) => CargarFacturas();
        tp.Controls.Add(cboFiltro);

        gFacturas = UI.Grilla(10, 45, 960, 230);
        gFacturas.SelectionChanged += (s, e) => CargarDetalle();
        tp.Controls.Add(gFacturas);

        tp.Controls.Add(new Label { Text = "Detalle de la factura seleccionada:", Location = new Point(10, 285), AutoSize = true });
        gDetalle = UI.Grilla(10, 310, 960, 170);
        tp.Controls.Add(gDetalle);

        lblResumen = new Label { Location = new Point(10, 490), AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
        tp.Controls.Add(lblResumen);

        return tp;
    }

    void CargarFacturas()
    {
        string tp = cboFiltro.SelectedItem.ToString();
        var dt = Datos.Tabla(@"
SELECT v.Id AS Factura, v.Fecha, c.Nombre AS Cliente, ca.Nombre AS Cajero,
       v.TipoPago AS [Tipo de pago], v.SubTotal, v.Itbis, v.Total
FROM Ventas v
JOIN Clientes c ON c.Id = v.ClienteId
LEFT JOIN Cajeros ca ON ca.Id = v.CajeroId
WHERE (@tp = 'Todos' OR v.TipoPago = @tp)
ORDER BY v.Id DESC", ("@tp", tp));

        gFacturas.DataSource = dt;
        double suma = dt.AsEnumerable().Sum(r => Convert.ToDouble(r["Total"]));
        lblResumen.Text = $"{dt.Rows.Count} factura(s)   |   Total: {suma:N2}";
        CargarDetalle();
    }

    void CargarDetalle()
    {
        if (gDetalle == null) return;
        if (gFacturas.CurrentRow == null)
        {
            gDetalle.DataSource = null;
            return;
        }
        int id = Convert.ToInt32(gFacturas.CurrentRow.Cells["Factura"].Value);
        gDetalle.DataSource = Datos.Tabla(@"
SELECT p.Nombre AS Producto, d.Cantidad, d.Precio, d.SubTotal
FROM DetalleVentas d
JOIN Productos p ON p.Id = d.ProductoId
WHERE d.VentaId = @v", ("@v", id));
    }

    // ===================== PRODUCTOS =====================
    TabPage CrearTabProductos()
    {
        var tp = new TabPage("Productos");

        gProductos = UI.Grilla(10, 10, 600, 500);
        gProductos.SelectionChanged += (s, e) => SeleccionarProducto();
        tp.Controls.Add(gProductos);

        int x = 640;
        tp.Controls.Add(new Label { Text = "Nombre:", Location = new Point(x, 20), AutoSize = true });
        txtPNombre = new TextBox { Location = new Point(x, 45), Width = 320 };
        tp.Controls.Add(txtPNombre);

        tp.Controls.Add(new Label { Text = "Precio:", Location = new Point(x, 85), AutoSize = true });
        txtPPrecio = new TextBox { Location = new Point(x, 110), Width = 320 };
        tp.Controls.Add(txtPPrecio);

        tp.Controls.Add(new Label { Text = "Stock:", Location = new Point(x, 150), AutoSize = true });
        txtPStock = new TextBox { Location = new Point(x, 175), Width = 320 };
        tp.Controls.Add(txtPStock);

        var btnAgregar = new Button { Text = "Agregar como nuevo", Location = new Point(x, 225), Size = new Size(320, 36) };
        btnAgregar.Click += (s, e) => AgregarProducto();
        tp.Controls.Add(btnAgregar);

        var btnActualizar = new Button { Text = "Actualizar seleccionado", Location = new Point(x, 270), Size = new Size(320, 36) };
        btnActualizar.Click += (s, e) => ActualizarProducto();
        tp.Controls.Add(btnActualizar);

        var btnEliminar = new Button { Text = "Eliminar seleccionado", Location = new Point(x, 315), Size = new Size(320, 36) };
        btnEliminar.Click += (s, e) => EliminarProducto();
        tp.Controls.Add(btnEliminar);

        return tp;
    }

    void CargarProductos()
    {
        gProductos.DataSource = Datos.Tabla("SELECT Id, Nombre, Precio, Stock FROM Productos ORDER BY Nombre");
    }

    void SeleccionarProducto()
    {
        if (txtPNombre == null || gProductos.CurrentRow == null) return;
        var cells = gProductos.CurrentRow.Cells;
        productoSel = Convert.ToInt32(cells["Id"].Value);
        txtPNombre.Text = cells["Nombre"].Value.ToString();
        txtPPrecio.Text = Convert.ToDouble(cells["Precio"].Value).ToString("0.00", CultureInfo.InvariantCulture);
        txtPStock.Text = cells["Stock"].Value.ToString();
    }

    bool LeerProducto(out string nombre, out double precio, out int stock)
    {
        nombre = txtPNombre.Text.Trim();
        precio = 0;
        stock = 0;
        if (nombre == "") { Msg("Escribe el nombre del producto."); return false; }
        if (!double.TryParse(txtPPrecio.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out precio) || precio < 0)
        { Msg("Precio invalido."); return false; }
        if (!int.TryParse(txtPStock.Text, out stock) || stock < 0)
        { Msg("Stock invalido."); return false; }
        return true;
    }

    void AgregarProducto()
    {
        if (!LeerProducto(out string nombre, out double precio, out int stock)) return;
        Datos.Ejecutar("INSERT INTO Productos(Nombre,Precio,Stock) VALUES(@n,@p,@s)",
            ("@n", nombre), ("@p", precio), ("@s", stock));
        CargarProductos();
    }

    void ActualizarProducto()
    {
        if (productoSel == 0) { Msg("Selecciona un producto de la lista."); return; }
        if (!LeerProducto(out string nombre, out double precio, out int stock)) return;
        Datos.Ejecutar("UPDATE Productos SET Nombre=@n, Precio=@p, Stock=@s WHERE Id=@i",
            ("@n", nombre), ("@p", precio), ("@s", stock), ("@i", productoSel));
        CargarProductos();
    }

    void EliminarProducto()
    {
        if (productoSel == 0) { Msg("Selecciona un producto de la lista."); return; }
        if (MessageBox.Show("¿Eliminar este producto?", "Confirmar", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            Datos.Ejecutar("DELETE FROM Productos WHERE Id=@i", ("@i", productoSel));
            productoSel = 0;
            txtPNombre.Clear(); txtPPrecio.Clear(); txtPStock.Clear();
            CargarProductos();
        }
        catch (SqlException)
        {
            Msg("No se puede eliminar: el producto ya tiene ventas registradas.");
        }
    }

    // ===================== USUARIOS =====================
    TabPage CrearTabUsuarios()
    {
        var tp = new TabPage("Usuarios");

        gUsuarios = UI.Grilla(10, 10, 960, 400);
        tp.Controls.Add(gUsuarios);

        tp.Controls.Add(new Label { Text = "Nuevo rol:", Location = new Point(10, 435), AutoSize = true });
        cboRolUsuario = new ComboBox { Location = new Point(90, 431), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        cboRolUsuario.Items.AddRange(new object[] { "Admin", "Cajero", "Cliente" });
        cboRolUsuario.SelectedIndex = 1;
        tp.Controls.Add(cboRolUsuario);

        var btnRol = new Button { Text = "Cambiar rol", Location = new Point(260, 428), Size = new Size(140, 32) };
        btnRol.Click += (s, e) => CambiarRol();
        tp.Controls.Add(btnRol);

        var btnDel = new Button { Text = "Eliminar usuario", Location = new Point(410, 428), Size = new Size(160, 32) };
        btnDel.Click += (s, e) => EliminarUsuario();
        tp.Controls.Add(btnDel);

        return tp;
    }

    void CargarUsuarios()
    {
        gUsuarios.DataSource = Datos.Tabla("SELECT Id, Usuario, Nombre, Rol FROM Cajeros ORDER BY Rol, Nombre");
    }

    void CambiarRol()
    {
        if (gUsuarios.CurrentRow == null) { Msg("Selecciona un usuario."); return; }
        int id = Convert.ToInt32(gUsuarios.CurrentRow.Cells["Id"].Value);
        if (id == ses.Id) { Msg("No puedes cambiar tu propio rol."); return; }

        string rol = cboRolUsuario.SelectedItem.ToString();

        // Un Cliente necesita su registro en la tabla Clientes
        if (rol == "Cliente")
        {
            object ci = Datos.Escalar("SELECT ClienteId FROM Cajeros WHERE Id=@i", ("@i", id));
            if (ci == null || ci == DBNull.Value)
            {
                string nombre = gUsuarios.CurrentRow.Cells["Nombre"].Value.ToString();
                object nuevo = Datos.Escalar("INSERT INTO Clientes(Nombre) OUTPUT INSERTED.Id VALUES(@n)", ("@n", nombre));
                Datos.Ejecutar("UPDATE Cajeros SET ClienteId=@c WHERE Id=@i", ("@c", nuevo), ("@i", id));
            }
        }

        Datos.Ejecutar("UPDATE Cajeros SET Rol=@r WHERE Id=@i", ("@r", rol), ("@i", id));
        CargarUsuarios();
    }

    void EliminarUsuario()
    {
        if (gUsuarios.CurrentRow == null) { Msg("Selecciona un usuario."); return; }
        int id = Convert.ToInt32(gUsuarios.CurrentRow.Cells["Id"].Value);
        if (id == ses.Id) { Msg("No puedes eliminar tu propia cuenta."); return; }
        if (MessageBox.Show("¿Eliminar este usuario?", "Confirmar", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            Datos.Ejecutar("DELETE FROM Cajeros WHERE Id=@i", ("@i", id));
            CargarUsuarios();
        }
        catch (SqlException)
        {
            Msg("No se puede eliminar: este usuario ya tiene ventas registradas.");
        }
    }
}

// ---------------------------------------------------------------
// PANTALLA DEL CLIENTE (solo ve sus compras)
// ---------------------------------------------------------------
class FormCliente : Form
{
    readonly Sesion ses;
    DataGridView gFacturas, gDetalle;
    Label lblResumen;

    public FormCliente(Sesion ses)
    {
        this.ses = ses;
        Text = "Mis compras - " + ses.Nombre;
        ClientSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        Controls.Add(new Label
        {
            Text = "Bienvenido, " + ses.Nombre,
            Location = new Point(12, 10),
            AutoSize = true,
            Font = new Font("Segoe UI", 14, FontStyle.Bold)
        });

        Controls.Add(new Label { Text = "Mis facturas:", Location = new Point(12, 48), AutoSize = true });
        gFacturas = UI.Grilla(10, 72, 880, 200);
        gFacturas.SelectionChanged += (s, e) => CargarDetalle();
        Controls.Add(gFacturas);

        Controls.Add(new Label { Text = "Detalle de la factura seleccionada:", Location = new Point(12, 282), AutoSize = true });
        gDetalle = UI.Grilla(10, 306, 880, 190);
        Controls.Add(gDetalle);

        lblResumen = new Label { Location = new Point(12, 510), AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
        Controls.Add(lblResumen);

        CargarFacturas();
    }

    void CargarFacturas()
    {
        int clienteId = ses.ClienteId ?? -1;
        var dt = Datos.Tabla(@"
SELECT v.Id AS Factura, v.Fecha, ca.Nombre AS Cajero,
       v.TipoPago AS [Tipo de pago], v.SubTotal, v.Itbis, v.Total
FROM Ventas v
LEFT JOIN Cajeros ca ON ca.Id = v.CajeroId
WHERE v.ClienteId = @c
ORDER BY v.Id DESC", ("@c", clienteId));

        gFacturas.DataSource = dt;
        double suma = dt.AsEnumerable().Sum(r => Convert.ToDouble(r["Total"]));
        lblResumen.Text = dt.Rows.Count == 0
            ? "Aún no tienes compras registradas."
            : $"{dt.Rows.Count} compra(s)   |   Total comprado: {suma:N2}";
        CargarDetalle();
    }

    void CargarDetalle()
    {
        if (gDetalle == null) return;
        if (gFacturas.CurrentRow == null)
        {
            gDetalle.DataSource = null;
            return;
        }
        int id = Convert.ToInt32(gFacturas.CurrentRow.Cells["Factura"].Value);
        gDetalle.DataSource = Datos.Tabla(@"
SELECT p.Nombre AS Producto, d.Cantidad, d.Precio, d.SubTotal
FROM DetalleVentas d
JOIN Productos p ON p.Id = d.ProductoId
WHERE d.VentaId = @v", ("@v", id));
    }
}

class Item
{
    public int Id;
    public string Texto;
    public double Precio;
    public int Stock;
    public override string ToString() => Texto;
}

// ---------------------------------------------------------------
// FORMULARIO PUNTO DE VENTA
// ---------------------------------------------------------------
class FormVenta : Form
{
    const double TASA_ITBIS = 0.18;
    static readonly CultureInfo Cult = new CultureInfo("es-DO");

    readonly int cajeroId;
    readonly string cajeroNombre;

    ComboBox cboCliente, cboProducto, cboPago;
    TextBox txtCantidad, txtMonto;
    Button btnAgregar, btnEliminar, btnProcesar, btnImprimir;
    DataGridView grid;
    Label lblDevuelta, lblSubTotal, lblItbis, lblTotal;
    DataTable carrito;
    string ultimoTicket = "";

    double subTotal, itbis, total;

    public FormVenta(int cajeroId, string cajeroNombre)
    {
        this.cajeroId = cajeroId;
        this.cajeroNombre = cajeroNombre;

        Text = "Punto de Venta - Cajero: " + cajeroNombre;
        Size = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(220, 232, 224);
        Font = new Font("Consolas", 11);

        Controls.Add(new Label { Text = "Cliente:", Location = new Point(25, 30), AutoSize = true });
        cboCliente = new ComboBox { Location = new Point(110, 27), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
        Controls.Add(cboCliente);

        Controls.Add(new Label { Text = "Producto:", Location = new Point(25, 70), AutoSize = true });
        cboProducto = new ComboBox { Location = new Point(110, 67), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
        Controls.Add(cboProducto);

        Controls.Add(new Label { Text = "Cantidad:", Location = new Point(370, 30), AutoSize = true });
        txtCantidad = new TextBox { Location = new Point(370, 60), Width = 90, Text = "1" };
        Controls.Add(txtCantidad);

        btnAgregar = Boton("Agregar al carrito", 500, 25, 210, 45, Color.FromArgb(30, 30, 30));
        btnAgregar.Click += (s, e) => Agregar();
        btnEliminar = Boton("Eliminar del carrito", 730, 25, 220, 45, Color.FromArgb(30, 30, 30));
        btnEliminar.Click += (s, e) => Eliminar();
        Controls.Add(btnAgregar);
        Controls.Add(btnEliminar);

        carrito = new DataTable();
        carrito.Columns.Add("ProductoId", typeof(int));
        carrito.Columns.Add("Producto", typeof(string));
        carrito.Columns.Add("Precio", typeof(double));
        carrito.Columns.Add("Cantidad", typeof(int));
        carrito.Columns.Add("SubTotal", typeof(double));

        grid = new DataGridView
        {
            Location = new Point(25, 110),
            Size = new Size(925, 270),
            DataSource = carrito,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.FromArgb(160, 175, 205),
            RowHeadersVisible = false
        };
        Controls.Add(grid);
        grid.DataBindingComplete += (s, e) =>
        {
            if (grid.Columns.Contains("ProductoId")) grid.Columns["ProductoId"].Visible = false;
            grid.Columns["Precio"].DefaultCellStyle.Format = "N2";
            grid.Columns["SubTotal"].DefaultCellStyle.Format = "N2";
        };

        Controls.Add(new Label { Text = "Tipo de Pago:", Location = new Point(25, 410), AutoSize = true });
        cboPago = new ComboBox { Location = new Point(25, 437), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        cboPago.Items.AddRange(new object[] { "Efectivo", "Tarjeta", "Transferencia" });
        cboPago.SelectedIndex = 0;
        cboPago.SelectedIndexChanged += (s, e) => ActualizarDevuelta();
        Controls.Add(cboPago);

        Controls.Add(new Label { Text = "Monto Pagado:", Location = new Point(220, 410), AutoSize = true });
        txtMonto = new TextBox { Location = new Point(220, 437), Width = 140 };
        txtMonto.TextChanged += (s, e) => ActualizarDevuelta();
        Controls.Add(txtMonto);

        lblDevuelta = new Label { Text = "Devuelta: $0.00", Location = new Point(25, 500), AutoSize = true, Font = new Font("Consolas", 12, FontStyle.Bold) };
        Controls.Add(lblDevuelta);

        btnProcesar = Boton("Procesar Venta", 560, 405, 190, 40, Color.FromArgb(30, 30, 30));
        btnProcesar.Click += (s, e) => ProcesarVenta();
        btnImprimir = Boton("Imprimir Ticket", 765, 405, 185, 40, Color.FromArgb(30, 30, 30));
        btnImprimir.Enabled = false;
        btnImprimir.Click += (s, e) => ImprimirTicket();
        Controls.Add(btnProcesar);
        Controls.Add(btnImprimir);

        lblSubTotal = new Label { Text = "SubTotal: $0.00", Location = new Point(620, 465), AutoSize = true, Font = new Font("Consolas", 13, FontStyle.Bold) };
        lblItbis = new Label { Text = "ITBIS: $0.00", Location = new Point(620, 495), AutoSize = true, Font = new Font("Consolas", 13, FontStyle.Bold) };
        lblTotal = new Label { Text = "Total: $0.00", Location = new Point(620, 525), AutoSize = true, Font = new Font("Consolas", 14, FontStyle.Bold) };
        Controls.Add(lblSubTotal);
        Controls.Add(lblItbis);
        Controls.Add(lblTotal);

        CargarCombos();
    }

    Button Boton(string texto, int x, int y, int w, int h, Color fondo)
    {
        return new Button
        {
            Text = texto,
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = fondo,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
    }

    // ---------------- Carga de datos ----------------
    void CargarCombos()
    {
        using var c = Db.Abrir();

        var clientes = new List<Item>();
        using (var cmd = new SqlCommand("SELECT Id, Nombre FROM Clientes ORDER BY Nombre", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                clientes.Add(new Item { Id = r.GetInt32(0), Texto = r.GetString(1) });
        cboCliente.DataSource = clientes;

        var productos = new List<Item>();
        using (var cmd = new SqlCommand("SELECT Id, Nombre, Precio, Stock FROM Productos ORDER BY Nombre", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                productos.Add(new Item
                {
                    Id = r.GetInt32(0),
                    Texto = r.GetString(1),
                    Precio = Convert.ToDouble(r.GetValue(2)),
                    Stock = r.GetInt32(3)
                });
        cboProducto.DataSource = productos;
    }

    // ---------------- Carrito ----------------
    void Agregar()
    {
        if (cboProducto.SelectedItem is not Item prod) return;

        if (!int.TryParse(txtCantidad.Text, out int cant) || cant <= 0)
        {
            MessageBox.Show("Cantidad invalida.");
            return;
        }

        DataRow existente = carrito.AsEnumerable().FirstOrDefault(r => r.Field<int>("ProductoId") == prod.Id);
        int yaEnCarrito = existente?.Field<int>("Cantidad") ?? 0;

        if (yaEnCarrito + cant > prod.Stock)
        {
            MessageBox.Show($"Stock insuficiente. Disponible: {prod.Stock}");
            return;
        }

        if (existente != null)
        {
            existente["Cantidad"] = yaEnCarrito + cant;
            existente["SubTotal"] = (yaEnCarrito + cant) * prod.Precio;
        }
        else
        {
            carrito.Rows.Add(prod.Id, prod.Texto, prod.Precio, cant, cant * prod.Precio);
        }

        txtCantidad.Text = "1";
        Recalcular();
    }

    void Eliminar()
    {
        if (grid.CurrentRow == null) return;
        var fila = ((DataRowView)grid.CurrentRow.DataBoundItem).Row;
        carrito.Rows.Remove(fila);
        Recalcular();
    }

    void Recalcular()
    {
        subTotal = carrito.AsEnumerable().Sum(r => r.Field<double>("SubTotal"));
        itbis = subTotal * TASA_ITBIS;
        total = subTotal + itbis;

        lblSubTotal.Text = $"SubTotal: {subTotal.ToString("C2", Cult)}";
        lblItbis.Text = $"ITBIS: {itbis.ToString("C2", Cult)}";
        lblTotal.Text = $"Total: {total.ToString("C2", Cult)}";
        ActualizarDevuelta();
    }

    void ActualizarDevuelta()
    {
        double.TryParse(txtMonto.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double pagado);
        double dev = Math.Max(0, pagado - total);
        lblDevuelta.Text = $"Devuelta: {dev.ToString("C2", Cult)}";
    }

    // ---------------- Procesar venta ----------------
    void ProcesarVenta()
    {
        if (carrito.Rows.Count == 0)
        {
            MessageBox.Show("El carrito esta vacio.");
            return;
        }
        if (cboCliente.SelectedItem is not Item cliente) return;

        string tipoPago = cboPago.SelectedItem.ToString();

        double pagado;
        if (tipoPago == "Efectivo")
        {
            if (!double.TryParse(txtMonto.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out pagado) || pagado < total)
            {
                MessageBox.Show("El monto pagado es menor que el total.");
                return;
            }
        }
        else
        {
            pagado = total;
        }
        double devuelta = pagado - total;
        int ventaId;
        DateTime fecha = DateTime.Now;

        using (var c = Db.Abrir())
        using (var tx = c.BeginTransaction())
        {
            try
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO Ventas(Fecha,ClienteId,CajeroId,TipoPago,SubTotal,Itbis,Total,MontoPagado,Devuelta)
OUTPUT INSERTED.Id
VALUES(@f,@c,@caj,@t,@s,@i,@tot,@m,@d);";
                    cmd.Parameters.AddWithValue("@f", fecha);
                    cmd.Parameters.AddWithValue("@c", cliente.Id);
                    cmd.Parameters.AddWithValue("@caj", cajeroId);
                    cmd.Parameters.AddWithValue("@t", tipoPago);
                    cmd.Parameters.AddWithValue("@s", subTotal);
                    cmd.Parameters.AddWithValue("@i", itbis);
                    cmd.Parameters.AddWithValue("@tot", total);
                    cmd.Parameters.AddWithValue("@m", pagado);
                    cmd.Parameters.AddWithValue("@d", devuelta);
                    ventaId = Convert.ToInt32(cmd.ExecuteScalar());
                }

                foreach (DataRow r in carrito.Rows)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO DetalleVentas(VentaId,ProductoId,Cantidad,Precio,SubTotal)
VALUES(@v,@p,@c,@pr,@s);
UPDATE Productos SET Stock = Stock - @c WHERE Id = @p;";
                    cmd.Parameters.AddWithValue("@v", ventaId);
                    cmd.Parameters.AddWithValue("@p", r.Field<int>("ProductoId"));
                    cmd.Parameters.AddWithValue("@c", r.Field<int>("Cantidad"));
                    cmd.Parameters.AddWithValue("@pr", r.Field<double>("Precio"));
                    cmd.Parameters.AddWithValue("@s", r.Field<double>("SubTotal"));
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                MessageBox.Show("Error al guardar la venta: " + ex.Message);
                return;
            }
        }

        ultimoTicket = ConstruirTicket(ventaId, fecha, cliente.Texto, tipoPago, pagado, devuelta);
        btnImprimir.Enabled = true;

        MessageBox.Show($"Venta #{ventaId} guardada.\nDevuelta: {devuelta.ToString("C2", Cult)}");

        carrito.Rows.Clear();
        txtMonto.Clear();
        Recalcular();
        CargarCombos(); // refresca stock
    }

    // ---------------- Ticket ----------------
    string ConstruirTicket(int id, DateTime fecha, string cliente, string pago, double pagado, double devuelta)
    {
        var sb = new StringBuilder();
        sb.AppendLine("        PUNTO DE VENTA");
        sb.AppendLine("--------------------------------");
        sb.AppendLine($"Ticket #{id}");
        sb.AppendLine($"Fecha: {fecha:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Cliente: {cliente}");
        sb.AppendLine($"Cajero: {cajeroNombre}");
        sb.AppendLine("--------------------------------");
        foreach (DataRow r in carrito.Rows)
        {
            sb.AppendLine(r.Field<string>("Producto"));
            sb.AppendLine($"  {r.Field<int>("Cantidad")} x {r.Field<double>("Precio"):N2}  = {r.Field<double>("SubTotal"):N2}");
        }
        sb.AppendLine("--------------------------------");
        sb.AppendLine($"SubTotal: {subTotal:N2}");
        sb.AppendLine($"ITBIS 18%: {itbis:N2}");
        sb.AppendLine($"TOTAL: {total:N2}");
        sb.AppendLine($"Pago ({pago}): {pagado:N2}");
        sb.AppendLine($"Devuelta: {devuelta:N2}");
        sb.AppendLine("--------------------------------");
        sb.AppendLine("   Gracias por su compra!");
        return sb.ToString();
    }

    void ImprimirTicket()
    {
        var doc = new PrintDocument();
        doc.PrintPage += (s, e) =>
        {
            using var f = new Font("Consolas", 10);
            e.Graphics.DrawString(ultimoTicket, f, Brushes.Black, 20, 20);
        };
        using var preview = new PrintPreviewDialog { Document = doc, Width = 600, Height = 700 };
        preview.ShowDialog();
    }
}
