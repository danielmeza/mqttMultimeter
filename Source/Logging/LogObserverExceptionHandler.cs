using System;
using System.Diagnostics;
using System.Reactive.Concurrency;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace mqttMultimeter.Logging;

public class LogObserverExceptionHandler(ILogger logger) : IObserver<Exception>
{
    public void OnNext(Exception value)
    {
        if (Debugger.IsAttached)
            Debugger.Break();

        logger.LogError(value, "MyRxHandler Error");
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            throw value;
        });
    }

    public void OnError(Exception error)
    {
        if (Debugger.IsAttached)
            Debugger.Break();
        
        logger.LogError(error, "MyRxHandler OnError");

        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            throw error;
        });
    }

    public void OnCompleted()
    {
        // Normal terminal signal of the exception stream - nothing to report.
    }
}