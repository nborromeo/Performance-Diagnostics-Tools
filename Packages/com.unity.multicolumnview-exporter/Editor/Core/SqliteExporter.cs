using System.Collections.Generic;
using System.Linq;
using SQLite;

namespace MultiColumnViewExporter
{
    static class SqliteExporter
    {
        public static void Export(string dbPath, List<ExtractedTable> tables)
        {
            using var conn = new SQLiteConnection(dbPath);
            foreach (var table in tables)
                WriteTable(conn, table);
        }

        static void WriteTable(SQLiteConnection conn, ExtractedTable table)
        {
            if (table.Columns.Count == 0)
                return;

            var colDefs = table.Columns.Select(c => $"\"{c.Name}\" {c.SqlType}");
            conn.Execute($"DROP TABLE IF EXISTS \"{table.TableName}\"");
            conn.Execute($"CREATE TABLE \"{table.TableName}\" ({string.Join(", ", colDefs)})");

            var colNames = string.Join(", ", table.Columns.Select(c => $"\"{c.Name}\""));
            var placeholders = string.Join(", ", table.Columns.Select(_ => "?"));
            var insertSql = $"INSERT INTO \"{table.TableName}\" ({colNames}) VALUES ({placeholders})";

            conn.RunInTransaction(() =>
            {
                foreach (var row in table.Rows)
                    conn.Execute(insertSql, row);
            });
        }
    }
}
