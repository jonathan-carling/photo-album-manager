namespace PhotoAlbumManager.ViewModels;

public sealed class FilterOption
{
    public FilterOption(string label, int? value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public int? Value { get; }

    public override string ToString() => Label;
}
