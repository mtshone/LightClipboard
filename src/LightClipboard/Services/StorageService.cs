using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;
using LightClipboard.Models;

namespace LightClipboard.Services;

/// <summary>
/// 历史记录的持久化层（LiteDB 单文件数据库）。
/// 图片本体不入库，只在磁盘缓存目录保存 PNG，库中仅存路径与元数据。
/// </summary>
public sealed class StorageService : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ClipboardItem> _items;
    private readonly object _gate = new();
    private bool _disposed;

    public StorageService(string databasePath)
    {
        var connectionString = new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Shared,
            Upgrade = true,
        };

        _db = new LiteDatabase(connectionString);
        _items = _db.GetCollection<ClipboardItem>("items");
        _items.EnsureIndex(x => x.Hash);
        _items.EnsureIndex(x => x.LastUsedAt);
        _items.EnsureIndex(x => x.IsPinned);
    }

    /// <summary>按“收藏优先 + 最近使用时间倒序”返回全部条目。</summary>
    public List<ClipboardItem> GetAll()
    {
        lock (_gate)
        {
            return _items.FindAll()
                .OrderByDescending(x => x.IsPinned)
                .ThenByDescending(x => x.LastUsedAt)
                .ToList();
        }
    }

    public ClipboardItem? FindByHash(string hash)
    {
        lock (_gate)
        {
            return _items.FindOne(x => x.Hash == hash);
        }
    }

    /// <summary>插入或更新一条记录，返回带主键的实体。</summary>
    public ClipboardItem Upsert(ClipboardItem item)
    {
        lock (_gate)
        {
            if (item.Id == 0)
            {
                _items.Insert(item);
            }
            else
            {
                _items.Update(item);
            }

            return item;
        }
    }

    /// <summary>
    /// 写入一条新记录；若内容指纹已存在，则更新其时间戳并置顶（去重）。
    /// </summary>
    /// <returns>最终落库的实体，以及是否为新增。</returns>
    public (ClipboardItem Item, bool IsNew) AddOrTouch(ClipboardItem incoming)
    {
        lock (_gate)
        {
            var existing = _items.FindOne(x => x.Hash == incoming.Hash);
            if (existing != null)
            {
                existing.LastUsedAt = DateTime.Now;
                if (incoming.Type == ClipboardItemType.Text && !string.IsNullOrEmpty(incoming.Text))
                {
                    existing.Text = incoming.Text;
                }

                if (!string.IsNullOrEmpty(incoming.SourceApp))
                {
                    existing.SourceApp = incoming.SourceApp;
                }

                _items.Update(existing);
                return (existing, false);
            }

            _items.Insert(incoming);
            return (incoming, true);
        }
    }

    public void SetPinned(int id, bool pinned)
    {
        lock (_gate)
        {
            var item = _items.FindById(id);
            if (item == null)
            {
                return;
            }

            item.IsPinned = pinned;
            item.LastUsedAt = DateTime.Now;
            _items.Update(item);
        }
    }

    public void Touch(int id)
    {
        lock (_gate)
        {
            var item = _items.FindById(id);
            if (item == null)
            {
                return;
            }

            item.LastUsedAt = DateTime.Now;
            _items.Update(item);
        }
    }

    public ClipboardItem? GetById(int id)
    {
        lock (_gate)
        {
            return _items.FindById(id);
        }
    }

    /// <summary>删除单条记录，返回被删除的实体（供调用方清理图片文件）。</summary>
    public ClipboardItem? Delete(int id)
    {
        lock (_gate)
        {
            var item = _items.FindById(id);
            if (item == null)
            {
                return null;
            }

            _items.Delete(id);
            return item;
        }
    }

    /// <summary>
    /// 清空历史。<paramref name="keepPinned"/> 为 true 时保留收藏项。
    /// </summary>
    /// <returns>被删除的实体集合。</returns>
    public List<ClipboardItem> Clear(bool keepPinned)
    {
        lock (_gate)
        {
            var all = _items.FindAll().ToList();
            var toDelete = keepPinned ? all.Where(x => !x.IsPinned).ToList() : all;
            foreach (var item in toDelete)
            {
                _items.Delete(item.Id);
            }

            return toDelete;
        }
    }

    /// <summary>
    /// 超出上限时裁剪最旧的非收藏记录。
    /// </summary>
    /// <returns>被删除的实体集合。</returns>
    public List<ClipboardItem> Prune(int maxItems)
    {
        lock (_gate)
        {
            var removable = _items.Find(x => !x.IsPinned)
                .OrderByDescending(x => x.LastUsedAt)
                .ToList();

            if (removable.Count <= maxItems)
            {
                return new List<ClipboardItem>();
            }

            var victims = removable.Skip(maxItems).ToList();
            foreach (var item in victims)
            {
                _items.Delete(item.Id);
            }

            return victims;
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            return _items.Count();
        }
    }

    /// <summary>把数据库文件压缩到最小体积。</summary>
    public void Shrink()
    {
        lock (_gate)
        {
            try
            {
                _db.Rebuild();
            }
            catch (Exception ex)
            {
                Log.Warn("数据库整理失败", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            try
            {
                _db.Checkpoint();
            }
            catch
            {
                // 忽略
            }

            _db.Dispose();
        }
    }
}
