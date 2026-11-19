using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HideFiles.App.Models;

/// <summary>
/// 支持批量添加并单次触发 Reset 通知的 ObservableCollection，
/// 避免导入海量数据时引发数千次 UI 重排卡死。
/// </summary>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    public ObservableRangeCollection() { }

    public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }

    public void AddRange(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));

        _suppressNotification = true;
        try
        {
            foreach (var item in items)
            {
                Add(item);
            }
        }
        finally
        {
            _suppressNotification = false;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
