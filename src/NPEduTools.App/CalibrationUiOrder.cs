namespace NPEduTools.App;

// Used only on the dispatcher: a new action supersedes an older read, including its failure.
internal sealed class CalibrationUiOrder
{
    private long _revision;
    public long Advance() => ++_revision;
    public bool Current(long revision) => revision == _revision;
}
