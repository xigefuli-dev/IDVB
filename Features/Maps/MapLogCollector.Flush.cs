namespace IDVBuff.Features.Maps;

public sealed partial class MapLogCollector
{
    /// <summary>
    /// Coalesces all flush requests for a session into one worker. A poisoned
    /// diagnostic entry used to make every subsequent Append queue another
    /// serializer task, creating an unbounded shutdown backlog.
    /// </summary>
    private Task RequestFlush(Session session)
    {
        lock (session.FlushTaskGate)
        {
            if (session.PersistenceDisabled)
                return session.PendingFlush;

            session.FlushRequested = true;
            if (!session.FlushLoopActive)
            {
                session.FlushLoopActive = true;
                session.PendingFlush = Task.Run(() => RunFlushLoopAsync(session));
            }
            return session.PendingFlush;
        }
    }

    private async Task RunFlushLoopAsync(Session session)
    {
        while (true)
        {
            lock (session.FlushTaskGate)
            {
                if (!session.FlushRequested || session.PersistenceDisabled)
                {
                    session.FlushLoopActive = false;
                    return;
                }
                session.FlushRequested = false;
            }

            await FlushCoreAsync(session).ConfigureAwait(false);
        }
    }
}
