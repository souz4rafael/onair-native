namespace OnAirNative.Models;

/// <summary>A labelled URL bookmark shown in the Controller → Browser tab.</summary>
public class QuickLink
{
    public string Label { get; set; } = "";
    public string Url   { get; set; } = "";

    public QuickLink() { }
    public QuickLink(string label, string url) { Label = label; Url = url; }

    public override string ToString() => Label;
}
