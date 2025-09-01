using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Unleasharp.DB.Base;
using Unleasharp.DB.Base.ExtensionMethods;
using Unleasharp.DB.Base.QueryBuilding;
using Unleasharp.DB.Base.SchemaDefinition;
using Unleasharp.ExtensionMethods;

namespace Unleasharp.DB.MSSQL;

/// <summary>
/// MSSQL-specific query builder that extends the generic <see cref="Unleasharp.DB.Base.Query{Query}"/>.
/// Provides MSSQL syntax and rendering for SQL statements.
/// </summary>
public class Query : Unleasharp.DB.Base.Query<Query> {
    /// <inheritdoc/>
    protected override DatabaseEngine _Engine { get { return DatabaseEngine.MSSQL; } }

    #region Custom MSSQL query data
    #endregion
    /// <summary>
    /// Represents the delimiter used to mark the init of a MSSQL field in a structured data format.
    /// </summary>
    public const string FieldDelimiterInit = "["; 
    /// <summary>
    /// Represents the delimiter used to mark the end of a MSSQL field in a structured data format.
    /// </summary>
    public const string FieldDelimiterEnd  = "]";
    /// <summary>
    /// The delimiter used for escaping values in MSSQL.
    /// </summary>
    public const string ValueDelimiter     = "'";

    /// <summary>
    /// Specifies which column value should be returned from MSSQL when performing an INSERT statement with a OUTPUT INSERTED clause.
    /// For example, in "INSERT INTO ... OUTPUT INSERTED.id;", this field indicates which column (e.g., "id") to return from the inserted rows.
    /// </summary>
    public string QueryOutputInserted { get; protected set; }

    #region Public query building methods overrides
    /// <inheritdoc/>
    public override Query Into<TableClass>() {
        foreach (MemberInfo member in typeof(TableClass).GetMembers()) {
            Column column = member.GetCustomAttribute<Column>();
            if (column != null) {
                if (column.PrimaryKey) {
                    this.QueryOutputInserted = column.Name;
                }
            }
        }

        return base.Into<TableClass>();
    }

    /// <inheritdoc/>
    public override Query From<TableClass>() {
        foreach (MemberInfo member in typeof(TableClass).GetMembers()) {
            Column column = member.GetCustomAttribute<Column>();
            if (column != null) {
                if (column.PrimaryKey) {
                    this.QueryOutputInserted = column.Name;
                }
            }
        }

        return base.From<TableClass>();
    }

    /// <inheritdoc/>
    public override Query Set<T>(Expression<Func<T, object>> expression, dynamic value, bool escape = true) {
        Type   tableType         = typeof(T);
        string dbColumnName      = ExpressionHelper.ExtractColumnName<T>(expression);
        string classPropertyName = ExpressionHelper.ExtractClassFieldName<T>(expression);
        string tableName         = tableType.GetTableName();

        MemberInfo member    = tableType.GetMember(classPropertyName).FirstOrDefault();

        if (member == null) {
            return this.Set(dbColumnName, value, escape);
        }

        SqlParameter param = this.__GetMemberInfoSqlParameter(value, member);

        return this.Set(new Where<Query> {
            Field       = new FieldSelector(dbColumnName),
            Value       = param,
            EscapeValue = true
        });
    }

    /// <inheritdoc/>
    public override Query Value<T>(T row, bool skipNullValues = true) where T : class {
        Type rowType = row.GetType();

        if (rowType.IsClass) {
            List<SqlParameter> rowValues = new List<SqlParameter>();

            foreach (FieldInfo field in rowType.GetFields()) {
                rowValues.Add(this.__GetMemberInfoSqlParameter(field.GetValue(row), field));
            }
            foreach (PropertyInfo property in rowType.GetProperties()) {
                rowValues.Add(this.__GetMemberInfoSqlParameter(property.GetValue(row), property));
            }

            return this.Value(
                rowValues
                    .Where(rowValue => rowValue != null)
                    .ToDictionary(
                        rowValue => rowValue.ParameterName, 
                        rowValue => rowValue as dynamic
                    )
            );
        }

        return this.Value(row.ToDynamicDictionary());
    }

    /// <inheritdoc/>
    public override Query Values<T>(List<T> rows, bool skipNullValues = true) where T : class {
        foreach (T row in rows) {
            this.Value<T>(row, skipNullValues);
        }

        return (Query) this;
    }

    /// <summary>
    /// Creates an <see cref="SqlParameter"/> for the specified member of a class, using metadata from the provided
    /// <see cref="MemberInfo"/>.
    /// </summary>
    /// <remarks>This method uses metadata from the <see cref="MemberInfo"/> to configure the parameter,
    /// including attributes such as <see cref="ColumnAttribute"/>. If the member is nullable, the underlying type is
    /// used for type determination. The method also maps the member's type to the appropriate SQL Server data
    /// type.</remarks>
    /// <param name="value">The value to be assigned to the parameter. If <paramref name="value"/> is <see langword="null"/>, it will be
    /// replaced with <see cref="DBNull.Value"/>.</param>
    /// <param name="memberInfo">The <see cref="MemberInfo"/> representing the class member for which the parameter is being created. This is
    /// used to determine the parameter's name, type, and additional metadata.</param>
    /// <returns>An <see cref="SqlParameter"/> configured with the appropriate name, value, and type based on the provided
    /// <paramref name="memberInfo"/> and its attributes. If the member is a primary key with a non-null constraint and
    /// the value is <see langword="null"/>, the method returns <see langword="null"/>.</returns>
    private SqlParameter __GetMemberInfoSqlParameter(object? value, MemberInfo memberInfo) {
        string          classFieldName   = memberInfo.Name;
        string          dbFieldName      = classFieldName;
        Type            memberInfoType   = memberInfo.GetDataType();
        Column?         column           = memberInfo.GetCustomAttribute<Column>();
        ColumnDataType? columnDataType   = null;
        SqlDbType?      dbColumnDataType = null;

        if (value == null) {
            value = DBNull.Value;
        }

        // Don't set null values to Primary Key columns
        // HOWEVER, be careful when mixing null and not-null values of Primary Key columns in the same insert
        if ((column != null && (column.PrimaryKey && column.NotNull)) && (value == null || value == DBNull.Value)) {
            return null;
        }

        if (value is Enum) {
            value = ((Enum)value).GetDescription();
        }

        if (Nullable.GetUnderlyingType(memberInfoType) != null) {
            memberInfoType = Nullable.GetUnderlyingType(memberInfoType);
        }

        if (column != null) {
            dbFieldName = column.Name;

            if (!string.IsNullOrEmpty(column.DataTypeString)) {
                columnDataType = this.GetColumnDataType(column.DataTypeString);
            }
            if (column.DataType != null) {
                columnDataType = column.DataType;
            }

            if (columnDataType == null) {
                columnDataType = memberInfoType.GetColumnType();
            }

            dbColumnDataType = this.GetSQLDataType(columnDataType.Value) ?? dbColumnDataType;
        }

        if (dbColumnDataType.HasValue) {
            return new SqlParameter {
                ParameterName = dbFieldName,
                Value         = value,
                SqlDbType     = dbColumnDataType.Value,
                SourceColumn  = dbFieldName,
            };
        }

        return new SqlParameter(dbFieldName, value);
    }
    #endregion

    #region Query rendering
    #region Query fragment rendering
    /// <inheritdoc/>
    public override void _RenderPrepared() {
        this._Render();

        string rendered = this.QueryPreparedString;

        foreach (string preparedDataItemKey in this.QueryPreparedData.Keys.Reverse()) {
            PreparedValue preparedDataItemValue = this.QueryPreparedData[preparedDataItemKey];
            object?       value                 = preparedDataItemValue.Value is SqlParameter ? (((SqlParameter)preparedDataItemValue.Value).Value) : preparedDataItemValue.Value;
            bool          escape                = preparedDataItemValue.Value is SqlParameter ? true                                                : preparedDataItemValue.EscapeValue;
            string        renderedValue         = "NULL";

            if (value != null && value != DBNull.Value) {
                renderedValue = true switch {
                    true when value is Enum   => this.__RenderWhereValue(((Enum)value).GetDescription(), escape),
                    true when value is byte[] => this.__RenderWhereValue($"0x{Convert.ToHexString((byte[])value)}", escape),
                                            _ => this.__RenderWhereValue(value, escape)
                };
            }
            rendered = rendered.Replace(preparedDataItemKey, renderedValue);
        }

        this.QueryRenderedString = rendered;
    }

    /// <summary>
    /// Renders a SQL SELECT fragment as a string, including subqueries and aliases if applicable.
    /// </summary>
    /// <remarks>This method handles both simple field rendering and subquery rendering. If the fragment
    /// includes a subquery, the subquery is rendered  in the context of the current query or its parent query, if
    /// available. If the fragment includes an alias, it is appended to the rendered  field using the "AS"
    /// keyword.</remarks>
    /// <param name="fragment">The <see cref="Select{T}"/> object representing the SQL SELECT fragment to render.</param>
    /// <returns>A string representation of the SQL SELECT fragment. If the fragment contains a subquery, the rendered subquery
    /// is enclosed in parentheses.  If an alias is specified, it is appended to the rendered field with the "AS"
    /// keyword.</returns>
    public string RenderSelect(Select<Query> fragment) {
        if (fragment.Subquery != null) {
            return "(" + fragment.Subquery.WithParentQuery(this.ParentQuery != null ? this.ParentQuery : this).Render() + ")";
        }

        return fragment.Field.Render() + (!string.IsNullOrWhiteSpace(fragment.Alias) ? $" AS {fragment.Alias}" : "");
    }

    /// <summary>
    /// Renders a SQL "FROM" clause based on the specified <see cref="From{T}"/> fragment.
    /// </summary>
    /// <remarks>This method supports rendering both subqueries and table names for use in SQL "FROM" clauses.
    /// If a subquery is provided, it is rendered with the appropriate parent query context. If a table name is
    /// provided, it can be optionally escaped using the <c>FieldDelimiter</c>.</remarks>
    /// <param name="fragment">The <see cref="From{T}"/> fragment containing the table, subquery, and alias information to be rendered into a
    /// SQL "FROM" clause. The <paramref name="fragment"/> must not be null.</param>
    /// <returns>A string representing the rendered SQL "FROM" clause. If the fragment contains a subquery, the subquery is
    /// rendered and enclosed in parentheses. If the fragment specifies a table, the table name is rendered, optionally
    /// escaped, and followed by the table alias if provided.</returns>
    public string RenderFrom(From<Query> fragment) {
        if (fragment.Subquery != null) {
            return "(" + fragment.Subquery.WithParentQuery(this.ParentQuery != null ? this.ParentQuery : this).Render() + ")";
        }

        string rendered = string.Empty;

        if (!string.IsNullOrWhiteSpace(fragment.Table)) {
            if (fragment.EscapeTable) {
                rendered = FieldDelimiterInit + fragment.Table + FieldDelimiterEnd;
            }
            else {
                rendered = fragment.Table;
            }
        }

        return rendered + (fragment.TableAlias != string.Empty ? $" {fragment.TableAlias}" : "");
    }

    /// <summary>
    /// Renders a SQL JOIN clause as a string based on the specified join fragment.
    /// </summary>
    /// <remarks>The table name is optionally escaped based on the <see cref="Join{T}.EscapeTable"/> property.
    /// The ON condition is rendered using the <see cref="RenderWhere"/> method.</remarks>
    /// <param name="fragment">The <see cref="Join{T}"/> object representing the join details, including the table name,  whether the table
    /// name should be escaped, and the join condition.</param>
    /// <returns>A string representing the SQL JOIN clause, including the table name and the ON condition.</returns>
    public string RenderJoin(Join<Query> fragment) {
        return $"{(fragment.EscapeTable ? FieldDelimiterInit + fragment.Table + FieldDelimiterEnd : fragment.Table)} ON {this.RenderWhere(fragment.Condition)}";
    }

    /// <summary>
    /// Renders a SQL "GROUP BY" clause based on the specified <see cref="GroupBy"/> fragment.
    /// </summary>
    /// <remarks>The method constructs the "GROUP BY" clause by combining the table and field names specified
    /// in the <paramref name="fragment"/>. If either the table or field name is null, empty, or consists only of
    /// whitespace, it will be excluded from the output. Escaping is applied to the table and field names if the
    /// <c>Escape</c> property of the <paramref name="fragment"/> is set to <see langword="true"/>.</remarks>
    /// <param name="fragment">The <see cref="GroupBy"/> fragment containing the table and field information to be rendered.</param>
    /// <returns>A string representing the "GROUP BY" clause, formatted with the table and field names. If the table or field
    /// names are marked for escaping, they will be enclosed with the appropriate delimiters.</returns>
    public string RenderGroupBy(GroupBy fragment) {
        List<string> toRender = new List<string>();

        if (!string.IsNullOrWhiteSpace(fragment.Field.Table)) {
            if (fragment.Field.Escape) {
                toRender.Add(FieldDelimiterInit + fragment.Field.Table + FieldDelimiterEnd);
            }
            else {
                toRender.Add(fragment.Field.Table);
            }
        }

        if (!string.IsNullOrWhiteSpace(fragment.Field.Field)) {
            if (fragment.Field.Escape) {
                toRender.Add(FieldDelimiterInit + fragment.Field.Field + FieldDelimiterEnd);
            }
            else {
                toRender.Add(fragment.Field.Field);
            }
        }

        return String.Join('.', toRender);
    }

    /// <summary>
    /// Renders a SQL WHERE clause based on the specified query fragment.
    /// </summary>
    /// <remarks>This method generates a SQL WHERE clause by combining the field, comparer, and value  (or
    /// value field) specified in the <paramref name="fragment"/>. If a subquery is provided,  it is rendered and
    /// included in the output. Special handling is applied for null values  and escaping, depending on the fragment's
    /// configuration.</remarks>
    /// <param name="fragment">The <see cref="Where{T}"/> object representing the query fragment to render.  This includes the field, comparer,
    /// value, and optional subquery or value field.</param>
    /// <returns>A <see cref="string"/> containing the rendered SQL WHERE clause.  The result is formatted based on the provided
    /// fragment's properties, including  handling subqueries, null values, and value escaping as necessary.</returns>
    public string RenderWhere(Where<Query> fragment) {
        if (fragment.Subquery != null) {
            return $"{fragment.Field.Render()} {fragment.Comparer.GetDescription()} ({fragment.Subquery.WithParentQuery(this.ParentQuery != null ? this.ParentQuery : this).Render()})";
        }

        List<string> toRender = new List<string>();

        toRender.Add(fragment.Field.Render());

        // We are comparing fields, not values
        if (fragment.ValueField != null) {
            toRender.Add(fragment.ValueField.Render());
        }
        else {
            if (fragment.Value == null) {
                fragment.Comparer = WhereComparer.IS;
                toRender.Add("NULL");
            }
            else {
                if (fragment.EscapeValue) {
                    toRender.Add(this.PrepareQueryValue(fragment.Value, fragment.EscapeValue));
                }
                else {
                    toRender.Add(this.__RenderWhereValue(fragment.Value, false));
                }
            }
        }

        return String.Join(fragment.Comparer.GetDescription(), toRender);
    }

    /// <summary>
    /// Renders a SQL "WHERE IN" clause based on the specified fragment.
    /// </summary>
    /// <remarks>If the <paramref name="fragment"/> contains a subquery, the method renders the "WHERE IN"
    /// clause using the subquery. Otherwise, it renders the clause using the provided values, applying value escaping
    /// if specified by the fragment.</remarks>
    /// <param name="fragment">The <see cref="WhereIn{T}"/> fragment containing the field, values, and optional subquery to be rendered into
    /// the "WHERE IN" clause.</param>
    /// <returns>A string representing the rendered "WHERE IN" clause. Returns an empty string if the fragment's <see
    /// cref="WhereIn{T}.Values"/> collection is null or empty.</returns>
    public string RenderWhereIn(WhereIn<Query> fragment) {
        if (fragment.Subquery != null) {
            return $"{fragment.Field.Render()} IN ({fragment.Subquery.WithParentQuery(this.ParentQuery != null ? this.ParentQuery : this).Render()})";
        }

        if (fragment.Values == null || fragment.Values.Count == 0) {
            return String.Empty;
        }

        List<string> toRender = new List<string>();

        foreach (dynamic fragmentValue in fragment.Values) {
            if (fragment.EscapeValue) {
                toRender.Add(this.PrepareQueryValue(fragmentValue, fragment.EscapeValue));
            }
            else {
                toRender.Add(__RenderWhereValue(fragmentValue, fragment.EscapeValue));
            }
        }

        return $"{fragment.Field.Render()} IN ({String.Join(",", toRender)})";
    }
    #endregion

    #region Query sentence rendering
    /// <inheritdoc/>
    protected override string _RenderCountSentence() {
        return "SELECT COUNT(*)";
    }

    /// <inheritdoc/>
    protected override string _RenderSelectSentence() {
        List<string> rendered = new List<string>();

        if (this.QuerySelect.Count > 0) {
            foreach (Select<Query> queryFragment in this.QuerySelect) {
                rendered.Add(this.RenderSelect(queryFragment));
            }
        }
        else {
            rendered.Add("*");
        }

        return $"SELECT {(this.QueryDistinct ? "DISTINCT " : "")}{string.Join(',', rendered)}";
    }

    /// <inheritdoc/>
    protected override string _RenderFromSentence() {
        List<string> rendered = new List<string>();

        foreach (From<Query> queryFragment in this.QueryFrom) {
            rendered.Add(this.RenderFrom(queryFragment));
        }

        return (rendered.Count > 0 ? "FROM " + string.Join(',', rendered) : "");
    }

    /// <inheritdoc/>
    protected override string _RenderJoinSentence() {
        List<string> rendered = new List<string>();
        foreach (Join<Query> queryFragment in this.QueryJoin) {
            rendered.Add(this.RenderJoin(queryFragment));
        }

        return (rendered.Count > 0 ? "JOIN " + string.Join(',', rendered) : "");
    }

    /// <inheritdoc/>
    protected override string _RenderWhereSentence() {
        List<string> rendered = new List<string>();
        foreach (Where<Query> queryFragment in this.QueryWhere) {
            if (rendered.Any()) {
                rendered.Add(queryFragment.Operator.GetDescription());
            }
            rendered.Add(this.RenderWhere(queryFragment));
        }
        foreach (WhereIn<Query> queryFragment in this.QueryWhereIn) {
            if (rendered.Any()) {
                rendered.Add(queryFragment.Operator.GetDescription());
            }
            rendered.Add(this.RenderWhereIn(queryFragment));
        }

        return (rendered.Count > 0 ? "WHERE " + string.Join(' ', rendered) : "");
    }

    /// <inheritdoc/>
    protected override string _RenderGroupSentence() {
        List<string> rendered = new List<string>();

        foreach (GroupBy queryFragment in this.QueryGroup) {
            rendered.Add(this.RenderGroupBy(queryFragment));
        }

        return (rendered.Count > 0 ? "GROUP BY " + string.Join(',', rendered) : "");
    }

    /// <inheritdoc/>
    protected override string _RenderHavingSentence() {
        List<string> rendered = new List<string>();

        foreach (Where<Query> queryFragment in this.QueryHaving) {
            rendered.Add(this.RenderWhere(queryFragment));
        }

        return (rendered.Count > 0 ? "HAVING " + string.Join(',', rendered) : "");
    }

    /// <inheritdoc/>
    protected override string _RenderOrderSentence() {
        List<string> rendered = new List<string>();

        if (this.QueryOrder != null) {
            foreach (OrderBy queryOrderItem in this.QueryOrder) {
                List<string> renderedSubset = new List<string>();

                renderedSubset.Add(queryOrderItem.Field.Render());

                if (queryOrderItem.Direction != OrderDirection.NONE) {
                    renderedSubset.Add(queryOrderItem.Direction.GetDescription());
                }

                rendered.Add(string.Join(' ', renderedSubset));
            }
        }

        return (rendered.Count > 0 ? "ORDER BY " + string.Join(',', rendered) : "");
    }

    /// <inheritdoc/>
    protected override string _RenderLimitSentence() {
        List<string> rendered = new List<string>();
        if (this.QueryLimit != null) {
            return 
                $"OFFSET     {this.QueryLimit.Offset} ROWS " + 
                $"FETCH NEXT {this.QueryLimit.Count } ROWS ONLY"
            ;
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    protected override string _RenderDeleteSentence() {
        From<Query> from = this.QueryFrom.FirstOrDefault();

        if (from != null) {
            return $"DELETE FROM {from.Table}{(!string.IsNullOrWhiteSpace(from.TableAlias) ? $" AS {from.TableAlias}" : "")}";
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    protected override string _RenderUpdateSentence() { 
        From<Query> from = this.QueryFrom.FirstOrDefault();

        if (from != null) {
            return $"UPDATE {this.RenderFrom(from)}";
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    protected override string _RenderSetSentence() {
        List<string> rendered = new List<string>();

        if (this.QueryOrder != null) {
            foreach (Where<Query> querySetItem in this.QuerySet) {
                querySetItem.Comparer = WhereComparer.EQUALS;

                rendered.Add(this.RenderWhere(querySetItem));
            }
        }

        return (rendered.Count > 0 ? "SET " + string.Join(',', rendered) : "");
    }

    /// <inheritdoc/>
    protected override string _RenderInsertIntoSentence() { 
        From<Query> from = this.QueryFrom.FirstOrDefault();

        if (from != null) {
            return 
                $"INSERT INTO {from.Table} ({string.Join(',', this.QueryColumns)})" +
                ((!string.IsNullOrWhiteSpace(QueryOutputInserted) ? $" OUTPUT INSERTED.{QueryOutputInserted}" : ""))
            ;
        }

        return string.Empty;
    }

    /// <inheritdoc/>
    protected override string _RenderInsertValuesSentence() {
        List<string> rendered = new List<string>();

        if (this.QueryValues != null) {
            foreach (Dictionary<string, dynamic> queryValue in QueryValues) {
                List<string> toRender = new List<string>();

                // In order to get a valid query, insert the values in the same column order
                foreach (string queryColumn in this.QueryColumns) {
                    if (queryValue.ContainsKey(queryColumn) && queryValue[queryColumn] != null) {
                        if (queryValue[queryColumn] != null && queryValue[queryColumn] is SqlParameter) {
                            string preparedQuerylabel = this.GetNextPreparedQueryValueLabel();
                            (queryValue[queryColumn] as SqlParameter).ParameterName = preparedQuerylabel;

                            toRender.Add(this.PrepareQueryValue(queryValue[queryColumn], false));
                            continue;
                        }

                        if (queryValue[queryColumn] != null && queryValue[queryColumn] is Enum) {
                            toRender.Add(this.PrepareQueryValue(queryValue[queryColumn], false));
                        }
                        else {
                            toRender.Add(this.PrepareQueryValue(queryValue[queryColumn], true));
                        }
                    }
                    else {
                        toRender.Add("NULL");
                    }
                }

                rendered.Add($"({string.Join(",", toRender)})");
            }
        }

        return (rendered.Count > 0 ? $"VALUES {string.Join(',', rendered)}" : "");
    }

    /// <inheritdoc/>
    protected override string _RenderCreateSentence<T>() {
        return this._RenderCreateSentence(typeof(T));
    }

    /// <inheritdoc/>
    protected override string _RenderCreateSentence(Type tableType) {
        Table typeTable = tableType.GetCustomAttribute<Table>();
        if (typeTable == null) {
            throw new InvalidOperationException("Missing [Table] attribute");
        }

        StringBuilder rendered = new StringBuilder();

        rendered.Append("CREATE ");
        if (typeTable.Temporary) {
            rendered.Append("TEMPORARY ");
        }

        rendered.Append("TABLE ");
        if (typeTable.IfNotExists) {
            rendered.Append("IF NOT EXISTS ");
        }
        rendered.Append($"{Query.FieldDelimiterInit}{typeTable.Name}{Query.FieldDelimiterEnd} (");

        IEnumerable<string?> tableColumnDefinitions = this.__GetTableColumnDefinitions(tableType);
        IEnumerable<string?> tableKeyDefinitions    = this.__GetTableKeyDefinitions(tableType);

        rendered.Append(string.Join(",", tableColumnDefinitions.Concat(tableKeyDefinitions ?? Enumerable.Empty<string>())));
        rendered.Append(")");

        // Table options
        var tableOptions = tableType.GetCustomAttributes<TableOption>();
        foreach (TableOption tableOption in tableOptions) {
            rendered.Append($" {tableOption.Name}={tableOption.Value}");
        }

        return rendered.ToString();
    }

    /// <summary>
    /// Gets the column definitions for a table type.
    /// </summary>
    /// <param name="tableType">The table type.</param>
    /// <returns>The column definitions as strings.</returns>
    private IEnumerable<string?> __GetTableColumnDefinitions(Type tableType) {
        PropertyInfo[] tableProperties = tableType.GetProperties();
        FieldInfo   [] tableFields     = tableType.GetFields();

        return tableProperties.Select(tableProperty => {
            return this.__GetColumnDefinition(tableProperty, tableProperty.GetCustomAttribute<Column>());
        }).Where(renderedColumn => renderedColumn != null);
    }

    /// <summary>
    /// Determines whether the specified table type has a property or field marked as a primary key.
    /// </summary>
    /// <param name="tableType">The type of the table to inspect for a primary key column.</param>
    /// <returns><see langword="true"/> if the table type contains at least one property or field with a <see cref="Column"/>
    /// attribute where <see cref="Column.PrimaryKey"/> is <see langword="true"/>; otherwise, <see langword="false"/>.</returns>
    private bool __TableHasPrimaryKeyColumn(Type tableType) {
        foreach (PropertyInfo tableProperty in tableType.GetProperties()) {
            Column column = tableProperty.GetCustomAttribute<Column>();

            if (column != null && column.PrimaryKey) {
                return true;
            }
        }
        foreach (FieldInfo tableField in tableType.GetFields()) {
            Column column = tableField.GetCustomAttribute<Column>();

            if (column != null && column.PrimaryKey) {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Generates a collection of SQL key constraint definitions for the specified table type.
    /// </summary>
    /// <remarks>This method inspects the custom attributes applied to the specified table type to generate SQL key
    /// constraint  definitions. Supported key attributes include: <list type="bullet"> <item><description><see
    /// cref="Key"/>: Defines a general key constraint.</description></item> <item><description><see cref="PrimaryKey"/>:
    /// Defines a primary key constraint.</description></item> <item><description><see cref="UniqueKey"/>: Defines a unique
    /// key constraint.</description></item> <item><description><see cref="ForeignKey"/>: Defines a foreign key constraint,
    /// including references to another table.</description></item> </list> The generated SQL definitions include details
    /// such as constraint names, column names, and optional index types  (e.g., BTREE, HASH). For foreign keys, additional
    /// clauses like <c>ON DELETE</c> and <c>ON UPDATE</c> are included  if specified.</remarks>
    /// <param name="tableType">The type representing the table for which key constraint definitions are generated.  This type must have attributes
    /// defining keys, such as <see cref="Key"/>, <see cref="PrimaryKey"/>,  <see cref="UniqueKey"/>, or <see
    /// cref="ForeignKey"/>.</param>
    /// <returns>An <see cref="IEnumerable{T}"/> of strings, where each string represents a SQL key constraint definition  (e.g.,
    /// primary key, unique key, foreign key) for the specified table type. If no key attributes are found,  the collection
    /// will be empty.</returns>
    private IEnumerable<string?> __GetTableKeyDefinitions(Type tableType) {
        List<string> definitions = new List<string>();

        foreach (PrimaryKey pKey in tableType.GetCustomAttributes<PrimaryKey>()) {
            if (!this.__TableHasPrimaryKeyColumn(tableType)) {
                definitions.Add(
                    $"CONSTRAINT {Query.FieldDelimiterInit}pk_{pKey.Name}{Query.FieldDelimiterEnd} PRIMARY KEY" +
                    $"({string.Join(", ", pKey.Columns.Select(column => $"{Query.FieldDelimiterInit}{tableType.GetColumnName(column)}{Query.FieldDelimiterEnd}"))})"
                );
            }
        }
        foreach (UniqueKey uKey in tableType.GetCustomAttributes<UniqueKey>()) {
            definitions.Add(
                $"CONSTRAINT {Query.FieldDelimiterInit}uk_{uKey.Name}{Query.FieldDelimiterEnd} UNIQUE " +
                $"({string.Join(", ", uKey.Columns.Select(column => $"{Query.FieldDelimiterInit}{tableType.GetColumnName(column)}{Query.FieldDelimiterEnd}"))})"
            );
        }
        foreach (ForeignKey fKey in tableType.GetCustomAttributes<ForeignKey>()) {
            definitions.Add(
                $"CONSTRAINT {Query.FieldDelimiterInit}fk_{fKey.Name}{Query.FieldDelimiterEnd} FOREIGN KEY " +
                $"({string.Join(", ", fKey.Columns.Select(column => $"{Query.FieldDelimiterInit}{column}{Query.FieldDelimiterEnd}"))}) " + 
                $" REFERENCES {Query.FieldDelimiterInit}{fKey.ReferencedTable}{Query.FieldDelimiterEnd}" +
                $"({string.Join(", ", fKey.ReferencedColumns.Select(column => $"{Query.FieldDelimiterInit}{column}{Query.FieldDelimiterEnd}"))})" + 
                $"{(!string.IsNullOrWhiteSpace(fKey.OnDelete) ? $" ON DELETE {fKey.OnDelete}" : "")}" + 
                $"{(!string.IsNullOrWhiteSpace(fKey.OnUpdate) ? $" ON UPDATE {fKey.OnUpdate}" : "")}" 
            );
        }

        return definitions;
    }

    /// <summary>
    /// Generates the SQL column definition string for a given property and table column.
    /// </summary>
    /// <remarks>The generated column definition includes the column name, data type, length, precision,
    /// constraints (e.g., NOT NULL, UNIQUE), default values, comments, and other attributes based on the provided
    /// <paramref name="tableColumn"/> metadata. Special handling is applied for nullable types, enums, and specific
    /// data types such as VARCHAR and CHAR.</remarks>
    /// <param name="property">The <see cref="PropertyInfo"/> representing the property to map to the column.</param>
    /// <param name="tableColumn">The <see cref="Column"/> object containing metadata about the table column.</param>
    /// <returns>A string representing the SQL column definition, or <see langword="null"/> if <paramref name="tableColumn"/> is
    /// <see langword="null"/>.</returns>
    private string? __GetColumnDefinition(PropertyInfo property, Column tableColumn) {
        if (tableColumn == null) {
            return null;
        }

        Type columnType = property.PropertyType;
        if (Nullable.GetUnderlyingType(columnType) != null) {
            columnType = Nullable.GetUnderlyingType(columnType);
        }

        string columnDataTypeString = tableColumn.DataTypeString ?? this.GetColumnDataTypeString(tableColumn.DataType);

        StringBuilder columnBuilder = new StringBuilder($"{Query.FieldDelimiterInit}{tableColumn.Name}{Query.FieldDelimiterEnd} {columnDataTypeString}");
        if (tableColumn.Length > 0)
            columnBuilder.Append($" ({tableColumn.Length}{(tableColumn.Precision > 0 ? $",{tableColumn.Precision}" : "")})");
        if (tableColumn.Length == -1)
            columnBuilder.Append($" (MAX)");
        if (columnType.IsEnum) {

            List<string> enumValues = new List<string>();

            bool first = true;
            foreach (Enum enumValue in Enum.GetValues(columnType)) {
                if (first) {
                    first = false;
                    continue;
                }
                string enumValueString = enumValue.ToString();

                if (!string.IsNullOrWhiteSpace(enumValue.GetDescription())) {
                    enumValues.Add(this.__RenderWhereValue(enumValue.GetDescription(), true));
                }
                else {
                    enumValues.Add(this.__RenderWhereValue(enumValue.ToString(), true));
                }
            }

            columnBuilder.Append($" CHECK({Query.FieldDelimiterInit}{tableColumn.Name}{Query.FieldDelimiterEnd} IN({string.Join(',', enumValues)}))");
        }

        if (tableColumn.AutoIncrement)
            columnBuilder.Append(" IDENTITY(1,1)");
        if (tableColumn.PrimaryKey)
            columnBuilder.Append(" PRIMARY KEY");
        if (tableColumn.NotNull)
            columnBuilder.Append(" NOT NULL");
        if (tableColumn.Unique && !tableColumn.PrimaryKey)
            columnBuilder.Append(" UNIQUE");
        if (tableColumn.Default != null)
            columnBuilder.Append($" DEFAULT {tableColumn.Default}");
        if (tableColumn.Comment != null)
            columnBuilder.Append($" COMMENT '{tableColumn.Comment}'");
        return columnBuilder.ToString();
    }

    /// <inheritdoc/>
    protected override string _RenderSelectExtraSentence() {
        return string.Empty;
    }
    #endregion

    #endregion

    #region Helper functions
    /// <summary>
    /// Converts the specified value into a string representation suitable for use in a "WHERE" clause, optionally
    /// applying escaping for certain types.
    /// </summary>
    /// <remarks>This method is designed to format values for use in SQL-like "WHERE" clauses. The delimiters
    /// applied  during escaping are determined by the implementation.</remarks>
    /// <param name="value">The value to be rendered. Supported types include <see cref="string"/>, <see cref="DateTime"/>, and <see
    /// cref="Enum"/>.</param>
    /// <param name="escape"><see langword="true"/> to apply escaping to the value (e.g., enclosing it in delimiters for strings and dates); 
    /// otherwise, <see langword="false"/>.</param>
    /// <returns>A string representation of the value. For <see cref="string"/> and <see cref="DateTime"/> values,  the result is
    /// enclosed in delimiters if <paramref name="escape"/> is <see langword="true"/>.  For <see cref="Enum"/> values,
    /// the result is the description of the enumeration value, enclosed in delimiters.  For other types, the result is
    /// the value's string representation.</returns>
    public string __RenderWhereValue(dynamic value, bool escape) {
        if (value is string
            ||
            value is DateTime
        ) {
            if (escape) {
                return $"{ValueDelimiter}{value}{ValueDelimiter}";
            }
        }
        if (value is Enum) {
            return $"{ValueDelimiter}{((Enum)value).GetDescription()}{ValueDelimiter}";
        }

        return value.ToString();
    }

    /// <summary>
    /// Converts a nullable <see cref="ColumnDataType"/> value to its corresponding SQL Server data type string.
    /// </summary>
    /// <remarks>This method maps logical column data types to their SQL Server equivalents based on common usage
    /// patterns. For instance, <see cref="ColumnDataType.Int32"/> maps to "INT", while <see
    /// cref="ColumnDataType.Text"/> maps to "NVARCHAR".</remarks>
    /// <param name="type">The nullable <see cref="ColumnDataType"/> to convert. Represents the logical data type of a database column.</param>
    /// <returns>A string representing the SQL Server data type that corresponds to the specified <paramref name="type"/>. For
    /// example, "INT" for numeric types, "NVARCHAR" for textual types, and "VARBINARY" for binary data.</returns>
    /// <exception cref="NotSupportedException">Thrown if the specified <paramref name="type"/> is not supported by SQL Server or is <c>null</c>.</exception>
    public string GetColumnDataTypeString(ColumnDataType? type) {
        return type switch {
            ColumnDataType.Boolean   => "BIT",
            ColumnDataType.Int16     => "SMALLINT",
            ColumnDataType.Int       => "INT",
            ColumnDataType.Int32     => "INT",
            ColumnDataType.Int64     => "BIGINT",
            ColumnDataType.UInt16    => "SMALLINT",
            ColumnDataType.UInt      => "INT",
            ColumnDataType.UInt32    => "INT",
            ColumnDataType.UInt64    => "BIGINT",
            ColumnDataType.Decimal   => "DECIMAL",
            ColumnDataType.Float     => "REAL",
            ColumnDataType.Double    => "FLOAT",
            ColumnDataType.Text      => "NVARCHAR",
            ColumnDataType.Char      => "CHAR",
            ColumnDataType.Varchar   => "NVARCHAR",
            ColumnDataType.Enum      => "NVARCHAR",
            ColumnDataType.Date      => "DATE",
            ColumnDataType.DateTime  => "DATETIME2",
            ColumnDataType.Time      => "TIME",
            ColumnDataType.Timestamp => "DATETIMEOFFSET",
            ColumnDataType.Binary    => "VARBINARY",
            ColumnDataType.Guid      => "UNIQUEIDENTIFIER",
            ColumnDataType.Json      => "NVARCHAR", // SQL Server 2016+ supports JSON functions but no native type
            ColumnDataType.Xml       => "XML",

            _ => throw new NotSupportedException($"MSSQL does not support {type}")
        };
    }

    /// <summary>
    /// Maps a <see cref="ColumnDataType"/> to its corresponding <see cref="SqlDbType"/>.
    /// </summary>
    /// <remarks>This method provides a mapping between application-specific column data types and SQL Server
    /// data types. If the provided <paramref name="type"/> does not have  a defined mapping, the method returns 
    /// <see langword="null"/>.</remarks>
    /// <param name="type">The <see cref="ColumnDataType"/> to be mapped.</param>
    /// <returns>The corresponding <see cref="SqlDbType"/> for the specified <paramref name="type"/>,  or <see langword="null"/>
    /// if the mapping is not defined.</returns>
    public SqlDbType? GetSQLDataType(ColumnDataType type) {
        return type switch {
            ColumnDataType.Boolean   => SqlDbType.Bit,
            ColumnDataType.Int16     => SqlDbType.SmallInt,
            ColumnDataType.Int       => SqlDbType.Int,
            ColumnDataType.Int32     => SqlDbType.Int,
            ColumnDataType.Int64     => SqlDbType.BigInt,
            ColumnDataType.UInt16    => SqlDbType.SmallInt,
            ColumnDataType.UInt32    => SqlDbType.Int,
            ColumnDataType.UInt      => SqlDbType.BigInt,
            ColumnDataType.UInt64    => SqlDbType.BigInt,
            ColumnDataType.Decimal   => SqlDbType.Decimal,
            ColumnDataType.Float     => SqlDbType.Real,
            ColumnDataType.Double    => SqlDbType.Float,
            ColumnDataType.Text      => SqlDbType.NVarChar,
            ColumnDataType.Char      => SqlDbType.Char,
            ColumnDataType.Varchar   => SqlDbType.NVarChar,
            ColumnDataType.Enum      => SqlDbType.NVarChar,
            ColumnDataType.Date      => SqlDbType.Date,
            ColumnDataType.DateTime  => SqlDbType.DateTime2,
            ColumnDataType.Time      => SqlDbType.Time,
            ColumnDataType.Timestamp => SqlDbType.DateTimeOffset,
            ColumnDataType.Binary    => SqlDbType.VarBinary,
            ColumnDataType.Guid      => SqlDbType.UniqueIdentifier,
            ColumnDataType.Json      => SqlDbType.NVarChar,
            ColumnDataType.Xml       => SqlDbType.Xml,

            _ => null
        };
    }
    #endregion
}
