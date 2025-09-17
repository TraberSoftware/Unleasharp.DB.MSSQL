using Microsoft.Data.SqlClient;
using System;
using Unleasharp.DB.Base;

namespace Unleasharp.DB.MSSQL;

/// <summary>
/// Represents a connector for managing connections to a MSSQL database.
/// </summary>
/// <remarks>This class provides functionality to establish, manage, and terminate connections to a MSSQL
/// database. It extends the base functionality provided by <see cref="Unleasharp.DB.Base.Connector{TConnector,
/// TConnectionStringBuilder}"/>. Use this class to interact with a MSSQL database by providing a connection string or a
/// pre-configured <see cref="SqlConnectionStringBuilder"/>.</remarks>
public class Connector : Unleasharp.DB.Base.Connector<Connector, SqlConnection, SqlConnectionStringBuilder> {
    #region Default constructors
    /// <inheritdoc />
    public Connector(SqlConnectionStringBuilder stringBuilder) : base(stringBuilder) { }
    /// <inheritdoc />
    public Connector(string connectionString)                  : base(connectionString) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Connector"/> class using the specified MSSQL connection.
    /// </summary>
    /// <param name="connection">The <see cref="SqlConnection"/> instance to be used by the connector. Cannot be <see langword="null"/>.</param>
    public Connector(SqlConnection connection) {
        this.Connection = connection;
    }
    #endregion
}
