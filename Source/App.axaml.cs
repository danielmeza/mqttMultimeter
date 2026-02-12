using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using mqttMultimeter.Controls;
using mqttMultimeter.Logging;
using mqttMultimeter.Main;
using mqttMultimeter.Pages.Connection;
using mqttMultimeter.Pages.Inflight;
using mqttMultimeter.Pages.Inflight.Export;
using mqttMultimeter.Pages.Info;
using mqttMultimeter.Pages.Log;
using mqttMultimeter.Pages.PacketInspector;
using mqttMultimeter.Pages.Publish;
using mqttMultimeter.Pages.Subscriptions;
using mqttMultimeter.Pages.TopicExplorer;
using mqttMultimeter.Services.Data;
using mqttMultimeter.Services.Mqtt;
using mqttMultimeter.Services.State;
using mqttMultimeter.Services.Updates;
using ReactiveUI;
using ViewLocator = mqttMultimeter.Common.ViewLocator;

namespace mqttMultimeter;

public sealed class App : Application
{
    static MainViewModel? _mainViewModel;

    readonly StateService _stateService;
    
    readonly ILoggerFactory _loggerFactory;
    readonly ILogger<App> _logger;

    public App()
    {
        
        _loggerFactory = LoggerFactory.Create(builder =>
        {
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
#endif
        });
        
        _logger = _loggerFactory.CreateLogger<App>();
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        RxApp.DefaultExceptionHandler = new LogObserverExceptionHandler(_logger);
        
        this.AttachDeveloperTools(o =>
        {
            o.AddMicrosoftLoggerObservable(_loggerFactory);
        });
        
        var serviceProvider = new ServiceCollection()
            // Services
            .AddSingleton<MqttClientService>()
            .AddSingleton<AppUpdateService>()
            .AddSingleton<JsonSerializerService>()
            .AddSingleton<StateService>()
            .AddSingleton<InflightPageItemExportService>()
            // Pages
            .AddSingleton<ConnectionPageViewModel>()
            .AddSingleton<PublishPageViewModel>()
            .AddSingleton<SubscriptionsPageViewModel>()
            .AddSingleton<PacketInspectorPageViewModel>()
            .AddSingleton<TopicExplorerPageViewModel>()
            .AddSingleton<InflightPageViewModel>()
            .AddSingleton<LogPageViewModel>()
            .AddSingleton<InfoPageViewModel>()
            .AddSingleton<MainViewModel>()
            .AddTransient(typeof(ILogger<>), typeof(Logger<>))
            .AddSingleton(_loggerFactory)
            .BuildServiceProvider();

        var viewLocator = new ViewLocator();
        DataTemplates.Add(viewLocator);

        serviceProvider.GetRequiredService<AppUpdateService>().EnableUpdateChecks();
        _stateService = serviceProvider.GetRequiredService<StateService>();
        _mainViewModel = serviceProvider.GetService<MainViewModel>();
        
       
    }

    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Current!.RequestedThemeVariant = ThemeVariant.Dark;
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };

            mainWindow.Closing += (_, __) =>
            {
                _stateService.Write().GetAwaiter().GetResult();
            };

            desktop.MainWindow = mainWindow;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = _mainViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ShowException(Exception exception)
    {
        var viewModel = new ErrorBoxViewModel
        {
            Message = exception.Message,
            Exception = exception.ToString()
        };

        var errorBox = new ErrorBox
        {
            DataContext = viewModel
        };

        errorBox.Closed += (_, __) =>
        {
            // Consider using a Stack so that multiple contents like windows etc. can be stacked.
            _mainViewModel!.OverlayContent = null;
        };

        _mainViewModel!.OverlayContent = errorBox;
    }
    
    
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled exception");
        ShowException(e.Exception);
    }

    void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unobserved task exception");
        ShowException(e.Exception);
    }

}