using CommunityToolkit.Mvvm.ComponentModel;

using DbClone.Application.Enums;

namespace DbClone.UI.ViewModels;

/// <summary>
/// ViewModel for the schema objects panel showing counts and processing status.
/// </summary>
public sealed class ObjectsPanelViewModel : ObservableObject
{
    /// <summary>Object type entries with their counts and status.</summary>
    public ObservableObjectCollection Items { get; } = new();

    /// <summary>Resets all items to pending with zero counts.</summary>
    public void Reset()
    {
        Items.Clear();
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Table,
                0,
                EObjectStatus.Pending,
                "Tables in the source database (user schemas only)"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.View,
                0,
                EObjectStatus.Pending,
                "Views and materialized views"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Sequence,
                0,
                EObjectStatus.Pending,
                "Sequences (values are synced after data copy)"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Function,
                0,
                EObjectStatus.Pending,
                "Functions and procedures"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Trigger,
                0,
                EObjectStatus.Pending,
                "Triggers on tables"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Enum,
                0,
                EObjectStatus.Pending,
                "Custom enum types"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Domain,
                0,
                EObjectStatus.Pending,
                "Domain types"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.CompositeType,
                0,
                EObjectStatus.Pending,
                "Composite types"));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Index,
                0,
                EObjectStatus.Pending,
                "Secondary indexes only. Primary key indexes are always created with the table structure and are not counted here."));
        Items.Add(
            new ObjectItem(
                EDatabaseObjectType.Constraint,
                0,
                EObjectStatus.Pending,
                "Foreign keys + check constraints + unique constraints. " +
                "PK/unique/check are always created with the table structure; foreign keys are added after data copy."));
    }

    /// <summary>Updates the count for a given object type.</summary>
    public void SetCount(EDatabaseObjectType objectType, int count)
    {
        var item = Items.FirstOrDefault(i => i.ObjectType == objectType);
        if (item != null)
            item.Count = count;
        else
            Items.Add(new ObjectItem(objectType, count, EObjectStatus.Pending));
    }

    /// <summary>Marks an object type as done.
    /// Does not overwrite a Failed status — once failed, it stays failed.</summary>
    public void SetDone(EDatabaseObjectType objectType)
    {
        var item = Items.FirstOrDefault(i => i.ObjectType == objectType);
        if (item != null && item.Status != EObjectStatus.Failed)
            item.Status = EObjectStatus.Done;
    }

    /// <summary>Marks an object type as failed (finished with errors).</summary>
    public void SetFailed(EDatabaseObjectType objectType)
    {
        var item = Items.FirstOrDefault(i => i.ObjectType == objectType);
        if (item != null) item.Status = EObjectStatus.Failed;
    }

    /// <summary>Marks an object type as in-progress.
    /// Does not overwrite a Failed status — once failed, it stays failed.</summary>
    public void SetInProgress(EDatabaseObjectType objectType)
    {
        var item = Items.FirstOrDefault(i => i.ObjectType == objectType);
        if (item != null && item.Status != EObjectStatus.Failed)
            item.Status = EObjectStatus.InProgress;
    }
}

/// <summary>Observable collection helper for object items.</summary>
public sealed class
    ObservableObjectCollection : System.Collections.ObjectModel.ObservableCollection<ObjectItem>
{
}

/// <summary>
/// A single object type entry shown in the panel.
/// </summary>
public sealed partial class ObjectItem : ObservableObject
{
    /// <summary>Count of objects of this type.</summary>
    [ObservableProperty]
    private int _count;

    /// <summary>Processing status.</summary>
    [ObservableProperty]
    private EObjectStatus _status;

    /// <summary>Tooltip explaining what this count includes.</summary>
    public string Description { get; }

    /// <summary>Display name (pluralized) for the UI.</summary>
    public string DisplayName =>
        ObjectType switch
            {
                EDatabaseObjectType.Table => "Tables",
                EDatabaseObjectType.View => "Views",
                EDatabaseObjectType.Sequence => "Sequences",
                EDatabaseObjectType.Function => "Functions",
                EDatabaseObjectType.Index => "Indexes",
                EDatabaseObjectType.Constraint => "Constraints",
                EDatabaseObjectType.Trigger => "Triggers",
                EDatabaseObjectType.MaterializedView => "Materialized Views",
                EDatabaseObjectType.Enum => "Enums",
                EDatabaseObjectType.Domain => "Domains",
                EDatabaseObjectType.CompositeType => "Composite Types",
                EDatabaseObjectType.Schema => "Schemas",
                _ => ObjectType.ToString()
            };

    /// <summary>Object type enum value.</summary>
    public EDatabaseObjectType ObjectType { get; }

    /// <summary>Status glyph for display.</summary>
    public string StatusGlyph =>
        Status switch
            {
                EObjectStatus.Done => "\u2714", // ✔
                EObjectStatus.InProgress => "\u25B6", // ▶
                _ => "\u25CB" // ○
            };

    /// <summary>Creates a new object item.</summary>
    public ObjectItem(
        EDatabaseObjectType objectType,
        int count,
        EObjectStatus status,
        string? description = null)
    {
        ObjectType = objectType;
        Count = count;
        Status = status;
        Description = description ?? DisplayName;
    }

    partial void OnStatusChanged(EObjectStatus value) => OnPropertyChanged(nameof(StatusGlyph));
}
