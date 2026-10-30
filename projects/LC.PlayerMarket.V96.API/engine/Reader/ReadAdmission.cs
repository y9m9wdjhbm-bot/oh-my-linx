namespace MemoryHub.Reader;

// A waiting reader must observe cancellation without entering the LC read path.
// The monitor remains reentrant for existing nested source transactions.
internal sealed class ReadAdmission : IDisposable
{
    private object? gate;
    private ReadAdmission(object value)=>gate=value;
    internal static ReadAdmission Enter(object gate,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while(!Monitor.TryEnter(gate,25))token.ThrowIfCancellationRequested();
        try{token.ThrowIfCancellationRequested();return new(gate);}
        catch{Monitor.Exit(gate);throw;}
    }
    public void Dispose(){var value=Interlocked.Exchange(ref gate,null);if(value is not null)Monitor.Exit(value);}
}
