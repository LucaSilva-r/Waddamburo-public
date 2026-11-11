namespace Waddamburo.Editor;

public static class MovieTransport
{
    /// <summary>Finds the next distinct labelled frame in the requested direction without wrapping.</summary>
    public static int? AdjacentLabel(int frame, int direction, IEnumerable<int> labels) => direction < 0
        ? labels.Where(label => label < frame).OrderDescending().Select(label => (int?)label).FirstOrDefault()
        : labels.Where(label => label > frame).Order().Select(label => (int?)label).FirstOrDefault();
}
