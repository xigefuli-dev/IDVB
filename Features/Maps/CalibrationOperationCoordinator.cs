namespace IDVBuff.Features.Maps;

/// <summary>One recoverable calibration at a time; cancellation revokes old continuations.</summary>
public sealed class CalibrationOperationCoordinator
{
    private readonly object _gate=new();
    private Operation? _active;
    private static long _sequence;
    public Operation? TryStart()
    {
        lock(_gate)
        {
            if(_active is not null) return null;
            return _active=new Operation(this,Interlocked.Increment(ref _sequence));
        }
    }
    public void Cancel()
    {
        Operation? old;
        lock(_gate) {old=_active;_active=null;}
        old?.Cancel();
    }
    public sealed class Operation : IDisposable
    {
        private readonly CalibrationOperationCoordinator _owner;
        private readonly CancellationTokenSource _cancellation=new();
        private int _disposed;
        private int _terminal;
        internal Operation(CalibrationOperationCoordinator owner,long id) {_owner=owner;Id=id;Token=_cancellation.Token;}
        public long Id {get;}
        public CancellationToken Token {get;}
        public bool IsCurrent { get {lock(_owner._gate) return ReferenceEquals(_owner._active,this) && !_cancellation.IsCancellationRequested;} }
        public bool TryFinish() => Interlocked.Exchange(ref _terminal,1)==0;
        internal void Cancel() { lock(_owner._gate) {if(_disposed==0) _cancellation.Cancel();} }
        public void EnsureCurrent() { if(!IsCurrent) throw new OperationCanceledException(Token); }
        public void Dispose()
        {
            lock(_owner._gate)
            {
                if(Interlocked.Exchange(ref _disposed,1)!=0) return;
                if(ReferenceEquals(_owner._active,this)) _owner._active=null;
                _cancellation.Dispose();
            }
        }
    }

    public static async Task<T> AwaitOwnedResultAsync<T>(Task<T> work,CancellationToken token) where T:IDisposable
    {
        try {return await work.WaitAsync(token).ConfigureAwait(false);}
        catch
        {
            _=work.ContinueWith(completed =>
            {
                if(completed.Status==TaskStatus.RanToCompletion) completed.Result.Dispose();
                else _=completed.Exception;
            },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            throw;
        }
    }
}
