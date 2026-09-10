using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 保存用户调整后的数据表列宽，避免长文本查看习惯在重启程序后丢失。</summary>
    public sealed class ColumnLayoutStore
    {
        private readonly string _databasePath;

        /// <summary>XMZADD 20260831 初始化本地列宽存储，所有布局数据仅保存在当前用户的 SQLite 文件。</summary>
        public ColumnLayoutStore(string databasePath = null)
        {
            _databasePath = string.IsNullOrWhiteSpace(databasePath) ? AppPathService.GetLocalDatabasePath() : databasePath;
            EnsureSchema();
        }

        /// <summary>XMZADD 20260831 保存一个数据表列的用户宽度，后续同名列以最新人工调整为准。</summary>
        public void Save(string gridName, string columnKey, double width)
        {
            if (string.IsNullOrWhiteSpace(gridName))
            {
                throw new ArgumentException("数据表标识不能为空。", "gridName");
            }
            if (string.IsNullOrWhiteSpace(columnKey))
            {
                throw new ArgumentException("列标识不能为空。", "columnKey");
            }
            if (Double.IsNaN(width) || Double.IsInfinity(width) || width < 40D)
            {
                return;
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"INSERT OR REPLACE INTO LocalColumnLayouts
(GridName, ColumnKey, Width, UpdatedAt)
VALUES (@GridName, @ColumnKey, @Width, @UpdatedAt);";
                command.Parameters.AddWithValue("@GridName", gridName);
                command.Parameters.AddWithValue("@ColumnKey", columnKey);
                command.Parameters.AddWithValue("@Width", width);
                command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260831 读取指定数据表已保存的列宽，使表格长文本可按用户习惯展示。</summary>
        public IDictionary<string, double> Load(string gridName)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(gridName))
            {
                return result;
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT ColumnKey, Width FROM LocalColumnLayouts
WHERE GridName = @GridName AND Width >= 40;";
                command.Parameters.AddWithValue("@GridName", gridName);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result[reader["ColumnKey"].ToString()] = Convert.ToDouble(reader["Width"], CultureInfo.InvariantCulture);
                    }
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260831 创建本地列宽表，确保布局偏好不会写入 EOS 数据库。</summary>
        private void EnsureSchema()
        {
            string directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (SQLiteConnection connection = OpenConnection())
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS LocalColumnLayouts (
GridName TEXT NOT NULL,
ColumnKey TEXT NOT NULL,
Width REAL NOT NULL,
UpdatedAt TEXT NOT NULL,
PRIMARY KEY (GridName, ColumnKey)
);";
                command.ExecuteNonQuery();
            }
        }

        /// <summary>XMZADD 20260831 打开应用本地 SQLite 文件，列宽偏好与连接及快照使用同一受控位置。</summary>
        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;");
            connection.Open();
            return connection;
        }
    }
}
