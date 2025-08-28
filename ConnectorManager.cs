using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Text;
using Unleasharp.DB.Base;

namespace Unleasharp.DB.MSSQL;

/// <summary>
/// Manager class for MSSQL database connections that provides access to query builders
/// for constructing and executing SQL queries.
/// </summary>
public class ConnectorManager : 
    ConnectorManager<ConnectorManager, Connector, SqlConnectionStringBuilder, SqlConnection, QueryBuilder, Query>
{
    /// <inheritdoc />
    public ConnectorManager()                                         : base() { }

    /// <inheritdoc />
    public ConnectorManager(SqlConnectionStringBuilder stringBuilder) : base(stringBuilder) { }

    /// <inheritdoc />
    public ConnectorManager(string connectionString)                  : base(connectionString) { }
}
