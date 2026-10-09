using System;
using System.Collections.Generic;
using SQLite;

namespace MultiColumnViewExporter
{
    sealed class QueryResult
    {
        public string[] ColumnNames = Array.Empty<string>();
        public List<object[]> Rows = new();
        public string Error;
    }

    // Runs arbitrary user-supplied SQL and reads back results with no compile-time
    // schema, using sqlite-net's low-level SQLite3 statement API (the same API the
    // library's own typed Query<T>() uses internally) instead of the typed ORM layer.
    static class AdHocQueryRunner
    {
        public static QueryResult Run(string dbPath, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return new QueryResult { Error = "Enter a SQL query first." };

            try
            {
                using var conn = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
                var stmt = SQLite3.Prepare2(conn.Handle, sql);
                try
                {
                    var colCount = SQLite3.ColumnCount(stmt);
                    var names = new string[colCount];
                    for (var i = 0; i < colCount; i++)
                        names[i] = SQLite3.ColumnName16(stmt, i);

                    var rows = new List<object[]>();
                    while (SQLite3.Step(stmt) == SQLite3.Result.Row)
                    {
                        var values = new object[colCount];
                        for (var i = 0; i < colCount; i++)
                        {
                            values[i] = SQLite3.ColumnType(stmt, i) switch
                            {
                                SQLite3.ColType.Integer => SQLite3.ColumnInt64(stmt, i),
                                SQLite3.ColType.Float => SQLite3.ColumnDouble(stmt, i),
                                SQLite3.ColType.Null => null,
                                _ => SQLite3.ColumnString(stmt, i),
                            };
                        }

                        rows.Add(values);
                    }

                    return new QueryResult { ColumnNames = names, Rows = rows };
                }
                finally
                {
                    SQLite3.Finalize(stmt);
                }
            }
            catch (Exception ex)
            {
                return new QueryResult { Error = ex.Message };
            }
        }
    }
}
