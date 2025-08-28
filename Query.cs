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

public class Query : Unleasharp.DB.Base.Query<Query> {
    #region Custom MSSQL query data
    #endregion

    public const string FieldDelimiterInit = "[";
    public const string FieldDelimiterEnd  = "]";
    public const string ValueDelimiter     = "'";

    #region Public query building methods overrides
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

    public virtual Query Values<T>(List<T> rows, bool skipNullValues = true) where T : class {
        foreach (T row in rows) {
            this.Value<T>(row, skipNullValues);
        }

        return (Query) this;
    }

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

    public string RenderSelect(Select<Query> fragment) {
        if (fragment.Subquery != null) {
            return "(" + fragment.Subquery.WithParentQuery(this.ParentQuery != null ? this.ParentQuery : this).Render() + ")";
        }

        return fragment.Field.Render() + (!string.IsNullOrWhiteSpace(fragment.Alias) ? $" AS {fragment.Alias}" : "");
    }

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

    public string RenderJoin(Join<Query> fragment) {
        return $"{(fragment.EscapeTable ? FieldDelimiterInit + fragment.Table + FieldDelimiterEnd : fragment.Table)} ON {this.RenderWhere(fragment.Condition)}";
    }

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

    public string RenderFieldSelector(FieldSelector fragment) {
        List<string> toRender = new List<string>();

        if (!string.IsNullOrWhiteSpace(fragment.Table)) {
            if (fragment.Escape) {
                toRender.Add(FieldDelimiterInit + fragment.Table + FieldDelimiterEnd);
            }
            else {
                toRender.Add(fragment.Table);
            }
        }

        if (!string.IsNullOrWhiteSpace(fragment.Field)) {
            if (fragment.Escape) {
                toRender.Add(FieldDelimiterInit + fragment.Field + FieldDelimiterEnd);
            }
            else {
                toRender.Add(fragment.Field);
            }
        }

        return String.Join('.', toRender);
    }
    #endregion

    #region Query sentence rendering
    protected override string _RenderCountSentence() {
        return "SELECT COUNT(*)";
    }

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

        return "SELECT " + string.Join(',', rendered);
    }

    protected override string _RenderFromSentence() {
        List<string> rendered = new List<string>();

        foreach (From<Query> queryFragment in this.QueryFrom) {
            rendered.Add(this.RenderFrom(queryFragment));
        }

        return (rendered.Count > 0 ? "FROM " + string.Join(',', rendered) : "");
    }

    protected override string _RenderJoinSentence() {
        List<string> rendered = new List<string>();
        foreach (Join<Query> queryFragment in this.QueryJoin) {
            rendered.Add(this.RenderJoin(queryFragment));
        }

        return (rendered.Count > 0 ? "JOIN " + string.Join(',', rendered) : "");
    }

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

    protected override string _RenderGroupSentence() {
        List<string> rendered = new List<string>();

        foreach (GroupBy queryFragment in this.QueryGroup) {
            rendered.Add(this.RenderGroupBy(queryFragment));
        }

        return (rendered.Count > 0 ? "GROUP BY " + string.Join(',', rendered) : "");
    }

    protected override string _RenderHavingSentence() {
        List<string> rendered = new List<string>();

        foreach (Where<Query> queryFragment in this.QueryHaving) {
            rendered.Add(this.RenderWhere(queryFragment));
        }

        return (rendered.Count > 0 ? "HAVING " + string.Join(',', rendered) : "");
    }

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

    protected override string _RenderDeleteSentence() {
        From<Query> from = this.QueryFrom.FirstOrDefault();

        if (from != null) {
            return $"DELETE FROM {from.Table}{(!string.IsNullOrWhiteSpace(from.TableAlias) ? $" AS {from.TableAlias}" : "")}";
        }

        return string.Empty;
    }
    protected override string _RenderUpdateSentence() { 
        From<Query> from = this.QueryFrom.FirstOrDefault();

        if (from != null) {
            return $"UPDATE {this.RenderFrom(from)}";
        }

        return string.Empty;
    }

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

    protected override string _RenderInsertIntoSentence() { 
        From<Query> from = this.QueryFrom.FirstOrDefault();

        if (from != null) {
            return $"INSERT INTO {from.Table} ({string.Join(',', this.QueryColumns)})";
        }

        return string.Empty;
    }

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

        return (rendered.Count > 0 ? "VALUES " + string.Join(',', rendered) : "");
    }

    protected override string _RenderCreateSentence<T>() {
        return this._RenderCreateSentence(typeof(T));
    }

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

    private IEnumerable<string?> __GetTableColumnDefinitions(Type tableType) {
        PropertyInfo[] tableProperties = tableType.GetProperties();
        FieldInfo   [] tableFields     = tableType.GetFields();

        return tableProperties.Select(tableProperty => {
            return this.__GetColumnDefinition(tableProperty, tableProperty.GetCustomAttribute<Column>());
        }).Where(renderedColumn => renderedColumn != null);
    }

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

    private IEnumerable<string?> __GetTableKeyDefinitions(Type tableType) {
        List<string> definitions = new List<string>();

        foreach (PrimaryKey pKey in tableType.GetCustomAttributes<PrimaryKey>()) {
            if (!this.__TableHasPrimaryKeyColumn(tableType)) {
                definitions.Add(
                    $"CONSTRAINT {Query.FieldDelimiterInit}pk_{pKey.Name}{Query.FieldDelimiterEnd} PRIMARY KEY" +
                    $"({string.Join(", ", pKey.Columns.Select(column => $"{Query.FieldDelimiterInit}{this._GetKeyColumnName(tableType, column)}{Query.FieldDelimiterEnd}"))})"
                );
            }
        }
        foreach (UniqueKey uKey in tableType.GetCustomAttributes<UniqueKey>()) {
            definitions.Add(
                $"CONSTRAINT {Query.FieldDelimiterInit}uk_{uKey.Name}{Query.FieldDelimiterEnd} UNIQUE " +
                $"({string.Join(", ", uKey.Columns.Select(column => $"{Query.FieldDelimiterInit}{this._GetKeyColumnName(tableType, column)}{Query.FieldDelimiterEnd}"))})"
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

    protected override string _RenderSelectExtraSentence() {
        return string.Empty;
    }
    #endregion

    #endregion

    #region Helper functions
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
