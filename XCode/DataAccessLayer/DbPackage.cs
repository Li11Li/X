﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NewLife;
using NewLife.Data;
using NewLife.Log;
using NewLife.Model;
using NewLife.Reflection;
using NewLife.Serialization;
using XCode.Transform;

namespace XCode.DataAccessLayer
{
    /// <summary>
    /// 数据包。数据的备份与恢复
    /// </summary>
    public class DbPackage
    {
        #region 属性
        /// <summary>
        /// 数据库连接
        /// </summary>
        public DAL Dal { get; set; }

        /// <summary>
        /// 进度报告
        /// </summary>
        public Action<Int64, DbTable> OnProgress { get; set; }

        /// <summary>批量处理时，忽略单表错误，继续处理下一个。默认true</summary>
        public Boolean IgnoreError { get; set; } = true;

        /// <summary>批量处理时，忽略单页错误，继续处理下一个。默认false</summary>
        public Boolean IgnorePageError { get; set; }

        /// <summary>
        /// 性能追踪器
        /// </summary>
        public ITracer Tracer { get; set; } = DAL.GlobalTracer;
        #endregion

        #region 备份
        /// <summary>备份单表数据，抽取数据和写入文件双线程</summary>
        /// <remarks>
        /// 最大支持21亿行
        /// </remarks>
        /// <param name="table">数据表</param>
        /// <param name="stream">目标数据流</param>
        /// <returns></returns>
        public Int32 Backup(IDataTable table, Stream stream)
        {
            using var span = Tracer?.NewSpan("db:Backup", table.Name);

            // 并行写入文件，提升吞吐
            var writeFile = new WriteFileActor
            {
                Stream = stream,

                // 最多同时堆积数
                BoundedCapacity = 4,
                TracerParent = span,
            };

            // 自增
            var id = table.Columns.FirstOrDefault(e => e.Identity);
            if (id == null)
            {
                var pks = table.PrimaryKeys;
                if (pks != null && pks.Length == 1 && pks[0].DataType.IsInt()) id = pks[0];
            }
            var tableName = Dal.Db.FormatName(table);
            var sb = new SelectBuilder { Table = tableName };
            var connName = Dal.ConnName;

            // 总行数
            writeFile.Total = Dal.SelectCount(sb);
            WriteLog("备份[{0}/{1}]开始，共[{2:n0}]行", table, connName, writeFile.Total);

            IExtracter<DbTable> extracer = new PagingExtracter(Dal, tableName);
            if (id != null)
                extracer = new IdExtracter(Dal, tableName, id.ColumnName);

            var sw = Stopwatch.StartNew();
            var total = 0;
            try
            {
                foreach (var dt in extracer.Fetch())
                {
                    var count = dt.Rows.Count;
                    WriteLog("备份[{0}/{1}]数据 {2:n0} + {3:n0}", table, connName, extracer.Row, count);
                    if (count == 0) break;

                    // 进度报告
                    OnProgress?.Invoke(extracer.Row, dt);

                    // 消费数据
                    writeFile.Tell(dt);

                    total += count;
                }

                // 通知写入完成
                writeFile.Stop(-1);
            }
            catch (Exception ex)
            {
                span?.SetError(ex, table);
                throw;
            }

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            WriteLog("备份[{0}/{1}]完成，共[{2:n0}]行，耗时{3:n0}ms，速度{4:n0}tps", table, connName, total, ms, total * 1000L / ms);

            // 返回总行数
            return total;
        }

        /// <summary>备份单表数据到文件</summary>
        /// <param name="table">数据表</param>
        /// <param name="file">文件。.gz后缀时采用压缩</param>
        /// <returns></returns>
        public Int32 Backup(IDataTable table, String file = null)
        {
            if (file.IsNullOrEmpty()) file = table + ".table";

            var file2 = file.GetFullPath();
            file2.EnsureDirectory(true);

            WriteLog("备份[{0}/{1}]到文件 {2}", table, Dal.ConnName, file2);

            using var fs = new FileStream(file2, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            var rs = 0;
            if (file.EndsWithIgnoreCase(".gz"))
            {
#if NET40
                using var gs = new GZipStream(fs, CompressionMode.Compress, true);
#else
                using var gs = new GZipStream(fs, CompressionLevel.Optimal, true);
#endif
                rs = Backup(table, gs);
            }
            else
            {
                rs = Backup(table, fs);
            }

            // 截断文件
            fs.SetLength(fs.Position);

            return rs;
        }

        /// <summary>备份一批表到指定压缩文件</summary>
        /// <param name="tables">数据表集合</param>
        /// <param name="file">zip压缩文件</param>
        /// <param name="backupSchema">备份架构</param>
        /// <returns></returns>
        public Int32 BackupAll(IList<IDataTable> tables, String file, Boolean backupSchema = true)
        {
            if (tables == null) throw new ArgumentNullException(nameof(tables));

            using var span = Tracer?.NewSpan("db:BackupAll", file);

            // 过滤不存在的表
            var ts = Dal.Tables.Select(e => e.TableName).ToArray();
            tables = tables.Where(e => e.TableName.EqualIgnoreCase(ts)).ToList();
            var connName = Dal.ConnName;

            var count = 0;
            //if (tables == null) tables = Tables;
            if (tables.Count > 0)
            {
#if !NET40
                var file2 = file.GetFullPath();
                file2.EnsureDirectory(true);

                WriteLog("备份[{0}]到文件 {1}。{2}", connName, file2, tables.Join(",", e => e.Name));

                using var fs = new FileStream(file2, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create, true, Encoding.UTF8);

                // 临时关闭日志
                var old = Dal.Db.ShowSQL;
                Dal.Db.ShowSQL = false;
                Dal.Session.ShowSQL = false;
                try
                {
                    // 备份架构
                    if (backupSchema)
                    {
                        var xml = DAL.Export(tables);
                        var entry = zip.CreateEntry(connName + ".xml");
                        using var ms = entry.Open();
                        ms.Write(xml.GetBytes());
                    }

                    foreach (var item in tables)
                    {
                        try
                        {
                            var entry = zip.CreateEntry(item.Name + ".table");
                            using var ms = entry.Open();
                            Backup(item, ms);

                            count++;
                        }
                        catch (Exception ex)
                        {
                            if (!IgnoreError) throw;
                            XTrace.WriteException(ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    span?.SetError(ex, null);
                    throw;
                }
                finally
                {
                    Dal.Db.ShowSQL = old;
                    Dal.Session.ShowSQL = old;
                }
#endif
            }

            return count;
        }
        #endregion

        #region 恢复
        /// <summary>从数据流恢复数据</summary>
        /// <param name="stream">数据流</param>
        /// <param name="table">数据表</param>
        /// <returns></returns>
        public Int32 Restore(Stream stream, IDataTable table)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (table == null) throw new ArgumentNullException(nameof(table));

            using var span = Tracer?.NewSpan("db:Restore", table.Name);

            var writeDb = new WriteDbActor
            {
                Table = table,
                Dal = Dal,
                IgnorePageError = IgnorePageError,
                Log = Log,
                Tracer = Tracer,

                // 最多同时堆积数页
                BoundedCapacity = 4,
                TracerParent = span,
            };
            var connName = Dal.ConnName;

            var sw = Stopwatch.StartNew();
            var total = 0;
            try
            {
                // 二进制读写器
                var bn = new Binary
                {
                    EncodeInt = true,
                    Stream = stream,
                };

                var dt = new DbTable();
                dt.ReadHeader(bn);
                WriteLog("恢复[{0}/{1}]开始，共[{2:n0}]行", table.Name, connName, dt.Total);

                // 输出日志
                var cs = dt.Columns;
                var ts = dt.Types;
                for (var i = 0; i < cs.Length; i++)
                {
                    if (ts[i] == null || ts[i] == typeof(Object))
                    {
                        var dc = table.Columns.FirstOrDefault(e => e.ColumnName.EqualIgnoreCase(cs[i]));
                        if (dc != null) ts[i] = dc.DataType;
                    }
                }
                WriteLog("字段[{0}]：{1}", cs.Length, cs.Join());
                WriteLog("类型[{0}]：{1}", ts.Length, ts.Join(",", e => e?.Name));

                var row = 0;
                var pageSize = (Dal.Db as DbBase).BatchSize;
                while (true)
                {
                    // 读取数据
                    dt.ReadData(bn, Math.Min(dt.Total - row, pageSize));

                    var rs = dt.Rows;
                    if (rs == null || rs.Count == 0) break;

                    WriteLog("恢复[{0}/{1}]数据 {2:n0} + {3:n0}", table.Name, connName, row, rs.Count);

                    // 进度报告
                    OnProgress?.Invoke(row, dt);

                    // 批量写入数据库。克隆对象，避免被修改
                    writeDb.Tell(dt.Clone());

                    // 下一页
                    total += rs.Count;
                    if (rs.Count < pageSize) break;
                    row += pageSize;
                }

                // 通知写入完成
                writeDb.Stop(-1);
            }
            catch (Exception ex)
            {
                span?.SetError(ex, null);
                throw;
            }

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            WriteLog("恢复[{0}/{1}]完成，共[{2:n0}]行，耗时{3:n0}ms，速度{4:n0}tps", table.Name, connName, total, ms, total * 1000L / ms);

            // 返回总行数
            return total;
        }

        /// <summary>从文件恢复数据</summary>
        /// <param name="file">zip压缩文件</param>
        /// <param name="table">数据表</param>
        /// <param name="setSchema">是否设置数据表模型，自动建表</param>
        /// <returns></returns>
        public Int64 Restore(String file, IDataTable table, Boolean setSchema = true)
        {
            if (file.IsNullOrEmpty()) throw new ArgumentNullException(nameof(file));
            if (table == null) throw new ArgumentNullException(nameof(table));

            var file2 = file.GetFullPath();
            if (!File.Exists(file2)) return 0;
            file2.EnsureDirectory(true);

            WriteLog("恢复[{2}]到[{0}/{1}]", table, Dal.ConnName, file);

            if (setSchema) Dal.SetTables(table);

            var compressed = file.EndsWithIgnoreCase(".gz");
            return file2.AsFile().OpenRead(compressed, s => Restore(s, table));
        }

        /// <summary>从指定压缩文件恢复一批数据到目标库</summary>
        /// <param name="file">zip压缩文件</param>
        /// <param name="tables">数据表。为空时从压缩包读取xml模型文件</param>
        /// <param name="setSchema">是否设置数据表模型，自动建表</param>
        /// <returns></returns>
        public IDataTable[] RestoreAll(String file, IDataTable[] tables = null, Boolean setSchema = true)
        {
            if (file.IsNullOrEmpty()) throw new ArgumentNullException(nameof(file));
            //if (tables == null) throw new ArgumentNullException(nameof(tables));

            var file2 = file.GetFullPath();
            if (!File.Exists(file2)) return null;

            using var span = Tracer?.NewSpan("db:RestoreAll", file);

#if !NET40
            using var fs = new FileStream(file2, FileMode.Open);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, true, Encoding.UTF8);

            // 备份架构
            if (tables == null)
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWithIgnoreCase(".xml"));
                if (entry != null)
                {
                    using var ms = entry.Open();
                    tables = DAL.Import(ms.ToStr()).ToArray();
                }
            }

            WriteLog("恢复[{0}]从文件 {1}。数据表：{2}", Dal.ConnName, file2, tables?.Join(",", e => e.Name));

            if (setSchema) Dal.SetTables(tables);

            // 临时关闭日志
            var old = Dal.Db.ShowSQL;
            Dal.Db.ShowSQL = false;
            Dal.Session.ShowSQL = false;
            try
            {
                foreach (var item in tables)
                {
                    var entry = zip.GetEntry(item.Name + ".table");
                    if (entry != null && entry.Length > 0)
                    {
                        try
                        {
                            using var ms = entry.Open();
                            Restore(ms, item);
                        }
                        catch (Exception ex)
                        {
                            if (!IgnoreError) throw;
                            XTrace.WriteException(ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                span?.SetError(ex, null);
                throw;
            }
            finally
            {
                Dal.Db.ShowSQL = old;
                Dal.Session.ShowSQL = old;
            }
#endif

            return tables;
        }
        #endregion

        #region 同步
        /// <summary>同步单表数据</summary>
        /// <remarks>
        /// 把数据同一张表同步到另一个库
        /// </remarks>
        /// <param name="table">数据表</param>
        /// <param name="connName">目标连接名</param>
        /// <param name="syncSchema">同步架构</param>
        /// <param name="progress">进度回调，参数为已处理行数和当前页表</param>
        /// <returns></returns>
        public Int32 Sync(IDataTable table, String connName, Boolean syncSchema = true, Action<Int32, DbTable> progress = null)
        {
            if (connName.IsNullOrEmpty()) throw new ArgumentNullException(nameof(connName));
            if (table == null) throw new ArgumentNullException(nameof(table));

            using var span = Tracer?.NewSpan("db:Sync", $"{table.Name}->{connName}");

            var dal = DAL.Create(connName);

            var writeDb = new WriteDbActor
            {
                Table = table,
                Dal = dal,
                IgnorePageError = IgnorePageError,
                Log = Log,
                Tracer = Tracer,

                // 最多同时堆积数页
                BoundedCapacity = 4,
                TracerParent = span,
            };

            // 自增
            var id = table.Columns.FirstOrDefault(e => e.Identity);
            // 主键
            if (id == null)
            {
                var pks = table.PrimaryKeys;
                if (pks != null && pks.Length == 1 && pks[0].DataType.IsInt()) id = pks[0];
            }

            var total = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                // 表结构
                if (syncSchema) dal.SetTables(table);

                var sb = new SelectBuilder
                {
                    Table = Dal.Db.FormatName(table)
                };

                var row = 0L;
                var pageSize = (Dal.Db as DbBase).BatchSize;
                while (true)
                {
                    var sql = "";
                    // 分割数据页，自增或分页
                    if (id != null)
                    {
                        sb.Where = $"{id.ColumnName}>={row}";
                        sql = Dal.PageSplit(sb, 0, pageSize);
                    }
                    else
                        sql = Dal.PageSplit(sb, row, pageSize);

                    // 查询数据
                    var dt = Dal.Session.Query(sql, null);
                    if (dt == null || dt.Rows.Count == 0) break;

                    var count = dt.Rows.Count;
                    WriteLog("同步[{0}/{1}]数据 {2:n0} + {3:n0}", table.Name, Dal.ConnName, row, count);

                    // 进度报告
                    progress?.Invoke((Int32)row, dt);

                    // 消费数据
                    writeDb.Tell(dt);

                    // 下一页
                    total += count;
                    //if (count < pageSize) break;

                    // 自增分割时，取最后一行
                    if (id != null)
                        row = dt.Get<Int64>(count - 1, id.ColumnName) + 1;
                    else
                        row += pageSize;
                }

                // 通知写入完成
                writeDb.Stop(-1);
            }
            catch (Exception ex)
            {
                span?.SetError(ex, null);
                throw;
            }

            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            WriteLog("同步[{0}/{1}]完成，共[{2:n0}]行，耗时{3:n0}ms，速度{4:n0}tps", table.Name, Dal.ConnName, total, ms, total * 1000L / ms);

            // 返回总行数
            return total;
        }

        /// <summary>备份一批表到另一个库</summary>
        /// <param name="tables">表名集合</param>
        /// <param name="connName">目标连接名</param>
        /// <param name="syncSchema">同步架构</param>
        /// <returns></returns>
        public IDictionary<String, Int32> SyncAll(IDataTable[] tables, String connName, Boolean syncSchema = true)
        {
            if (connName.IsNullOrEmpty()) throw new ArgumentNullException(nameof(connName));
            if (tables == null) throw new ArgumentNullException(nameof(tables));

            using var span = Tracer?.NewSpan("db:SyncAll", connName);

            var dic = new Dictionary<String, Int32>();

            if (tables.Length == 0) return dic;

            // 同步架构
            if (syncSchema) DAL.Create(connName).SetTables(tables);

            // 临时关闭日志
            var old = Dal.Db.ShowSQL;
            Dal.Db.ShowSQL = false;
            Dal.Session.ShowSQL = false;
            try
            {
                foreach (var item in tables)
                {
                    try
                    {
                        dic[item.Name] = Sync(item, connName, false);
                    }
                    catch (Exception ex)
                    {
                        if (!IgnoreError) throw;
                        XTrace.WriteException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                span?.SetError(ex, null);
                throw;
            }
            finally
            {
                Dal.Db.ShowSQL = old;
                Dal.Session.ShowSQL = old;
            }

            return dic;
        }
        #endregion

        #region 并行Actor
        /// <summary>
        /// 高吞吐写文件Actor
        /// </summary>
        public class WriteFileActor : Actor
        {
            /// <summary>
            /// 数据流
            /// </summary>
            public Stream Stream { get; set; }

            /// <summary>
            /// 总数
            /// </summary>
            public Int32 Total { get; set; }

            /// <summary>
            /// 日志
            /// </summary>
            public ILog Log { get; set; }

            private Binary _Binary;
            private Boolean _writeHeader;

            /// <summary>
            /// 开始
            /// </summary>
            /// <returns></returns>
            public override Task Start()
            {
                // 二进制读写器
                _Binary = new Binary
                {
                    EncodeInt = true,
                    Stream = Stream,
                };

                return base.Start();
            }

            /// <summary>
            /// 接收消息，写入文件
            /// </summary>
            /// <param name="context"></param>
            /// <returns></returns>
            protected override Task ReceiveAsync(ActorContext context)
            {
                var dt = context.Message as DbTable;
                var bn = _Binary;

                // 写头部结构。没有数据时可以备份结构
                if (!_writeHeader)
                {
                    dt.Total = Total;
                    dt.WriteHeader(bn);

                    // 输出日志
                    var cs = dt.Columns;
                    var ts = dt.Types;
                    Log?.Info("字段[{0}]：{1}", cs.Length, cs.Join());
                    Log?.Info("类型[{0}]：{1}", ts.Length, ts.Join(",", e => e.Name));

                    _writeHeader = true;
                }

                var rs = dt.Rows;
                if (rs == null || rs.Count == 0) return null;

                // 写入文件
                dt.WriteData(bn);
                Stream.Flush();

                return null;
            }
        }

        /// <summary>
        /// 高吞吐写数据库Actor
        /// </summary>
        public class WriteDbActor : Actor
        {
            /// <summary>
            /// 数据库连接
            /// </summary>
            public DAL Dal { get; set; }

            /// <summary>
            /// 数据表
            /// </summary>
            public IDataTable Table { get; set; }

            /// <summary>批量处理时，忽略单页错误，继续处理下一个。默认false</summary>
            public Boolean IgnorePageError { get; set; }

            /// <summary>
            /// 日志
            /// </summary>
            public ILog Log { get; set; }

            /// <summary>
            /// 性能追踪器
            /// </summary>
            public ITracer Tracer { get; set; }

            private IDataColumn[] _Columns;

            /// <summary>
            /// 接收消息，批量插入
            /// </summary>
            /// <param name="context"></param>
            /// <returns></returns>
            protected override Task ReceiveAsync(ActorContext context)
            {
                if (context.Message is not DbTable dt) return null;

                // 匹配要写入的列
                if (_Columns == null)
                {
                    _Columns = Table.GetColumns(dt.Columns);

                    Log?.Info("数据表：{0}/{1}", Table.Name, Table);
                    Log?.Info("匹配列：{0}", _Columns.Join(",", e => e.ColumnName));
                }

                // 批量插入
                using var span = Tracer?.NewSpan($"db:{Dal.ConnName}:BatchInsert:{Table.TableName}");
                if (IgnorePageError)
                {
                    try
                    {
                        Dal.Session.Insert(Table, _Columns, dt.Cast<IExtend>());
                    }
                    catch (Exception ex)
                    {
                        span?.SetError(ex, dt.Rows?.Count);
                    }
                }
                else
                {
                    Dal.Session.Insert(Table, _Columns, dt.Cast<IExtend>());
                }

                return null;
            }
        }
        #endregion

        #region 日志
        /// <summary>
        /// 日志
        /// </summary>
        public ILog Log { get; set; }

        /// <summary>
        /// 写日志
        /// </summary>
        /// <param name="format"></param>
        /// <param name="args"></param>
        public void WriteLog(String format, params Object[] args) => Log?.Info(format, args);
        #endregion
    }
}