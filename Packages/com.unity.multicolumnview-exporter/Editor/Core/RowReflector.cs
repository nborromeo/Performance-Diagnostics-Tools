using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace MultiColumnViewExporter
{
    struct ColumnDef
    {
        public string Name;
        public string SqlType;
    }

    // Builds a SQL schema and per-row values from arbitrary row objects via reflection,
    // since a MultiColumnListView/TreeView's column bindCell closures aren't inspectable
    // and the underlying row type is unknown ahead of time.
    static class RowReflector
    {
        public static (List<ColumnDef> columns, List<MemberInfo> members) BuildSchema(object sampleRow)
        {
            var members = new List<MemberInfo>();
            var columns = new List<ColumnDef>();
            if (sampleRow == null)
                return (columns, members);

            var type = sampleRow.GetType();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                    continue;
                members.Add(prop);
                columns.Add(new ColumnDef { Name = SanitizeName(prop.Name), SqlType = SqlTypeFor(prop.PropertyType) });
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                members.Add(field);
                columns.Add(new ColumnDef { Name = SanitizeName(field.Name), SqlType = SqlTypeFor(field.FieldType) });
            }

            return (columns, members);
        }

        public static object[] ExtractValues(object row, List<MemberInfo> members)
        {
            var values = new object[members.Count];
            for (var i = 0; i < members.Count; i++)
            {
                object raw;
                try
                {
                    raw = members[i] is PropertyInfo p ? p.GetValue(row) : ((FieldInfo)members[i]).GetValue(row);
                }
                catch
                {
                    raw = null;
                }

                values[i] = ToSqlValue(raw);
            }

            return values;
        }

        static object ToSqlValue(object raw)
        {
            switch (raw)
            {
                case null:
                    return null;
                case bool b:
                    return b ? 1L : 0L;
                case sbyte:
                case byte:
                case short:
                case ushort:
                case int:
                case uint:
                case long:
                case ulong:
                    return Convert.ToInt64(raw);
                case float:
                case double:
                case decimal:
                    return Convert.ToDouble(raw);
                case string s:
                    return s;
                case Enum e:
                    return e.ToString();
                default:
                    return raw.ToString();
            }
        }

        static string SqlTypeFor(Type t)
        {
            if (t == typeof(bool) || IsIntegerType(t))
                return "INTEGER";
            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
                return "REAL";
            return "TEXT";
        }

        static bool IsIntegerType(Type t) =>
            t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) ||
            t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

        static string SanitizeName(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in name)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            var result = sb.ToString();
            return string.IsNullOrEmpty(result) ? "col" : result;
        }
    }
}
