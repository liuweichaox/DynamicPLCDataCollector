﻿using System.Collections.Concurrent;
using DynamicPLCDataCollector.Common;
using DynamicPLCDataCollector.DataStorages;
using DynamicPLCDataCollector.Models;
using DynamicPLCDataCollector.PLCClients;
using DynamicPLCDataCollector.Services;

/// <summary>
/// 数据采集器
/// </summary>
public class DataCollector
{
    private readonly IDeviceService _deviceService;
    private readonly IMetricTableConfigService _metricTableConfigService;
    private readonly ConcurrentDictionary<string, Task> _runningTasks;
    private readonly CancellationTokenSource _cts;
    private readonly List<IDataStorage> _dataStorages;
    private readonly List<IPLCClient> _plcClients;
    private readonly PLCClientFactory _plcClientFactory;
    private readonly DataStorageFactory _dataStorageFactory;
    
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="plcClientFactory"></param>
    /// <param name="dataStorageFactory"></param>
    public DataCollector(PLCClientFactory plcClientFactory, DataStorageFactory dataStorageFactory)
    {
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - 采集程序已启动...");
        _deviceService = new DeviceService();
        _metricTableConfigService = new MetricTableConfigService();
        _runningTasks  = new ConcurrentDictionary<string, Task>();
        _cts = new CancellationTokenSource();
        _dataStorages = new List<IDataStorage>();
        _plcClients = new List<IPLCClient>();
        _plcClientFactory = plcClientFactory;
        _dataStorageFactory = dataStorageFactory;
        ListenExitEvents();
    }
    
    /// <summary>
    /// 生成采集任务的 Key
    /// </summary>
    /// <param name="device"></param>
    /// <param name="config"></param>
    /// <returns></returns>
    private string GenerateTaskKey(Device device, MetricTableConfig config)
    {
        return $"{device.Code}_{config.TableName}";
    }
    
    /// <summary>
    /// 是否开始采集任务
    /// </summary>
    /// <param name="device"></param>
    /// <param name="config"></param>
    /// <returns></returns>
    private bool IsTaskRunningForDeviceAndConfig(Device device, MetricTableConfig config)
    {
        var taskKey = GenerateTaskKey(device, config);
        return _runningTasks.ContainsKey(taskKey);
    }
    
    /// <summary>
    /// 开始采集任务
    /// </summary>
    public async Task StartCollectionTasks()
    {
        var devices = await _deviceService.GetDevices();
        
        var metricTableConfigs = await _metricTableConfigService.GetMetricTableConfigs();
        
        foreach (var device in devices)
        {
            foreach (var metricTableConfig in metricTableConfigs)
            {
                if (metricTableConfig.IsEnabled && !IsTaskRunningForDeviceAndConfig(device, metricTableConfig))
                { 
                    StartCollectionTask(device, metricTableConfig);
                }
            }
        }
    }

    /// <summary>
    /// 开始单个采集任务
    /// </summary>
    /// <param name="device"></param>
    /// <param name="metricTableConfig"></param>
    private void StartCollectionTask(Device device, MetricTableConfig metricTableConfig)
    {
        var task = Task.Factory.StartNew(async () =>
        {
            var plcClient = _plcClientFactory(device.IpAddress, device.Port);
            
            _plcClients.Add(plcClient);

            var dataStorage = _dataStorageFactory(metricTableConfig);
            
            _dataStorages.Add(dataStorage);
            
            var connect = await plcClient.ConnectServerAsync();
            if (connect.IsSuccess)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - 连接到设备 {device.Code} 成功！");
            }
            else
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - 连接到设备 {device.Code} 失败：{connect.Message}");
            }
            
            while (true)
            {
                try
                {
                    if (!plcClient.IsConnected())
                    {
                        connect = await plcClient.ConnectServerAsync();
                        
                        if (connect.IsSuccess)
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - 重新连接到设备 {device.Code} 成功！");
                        }
                        else
                        {
                            throw new Exception($"重新连接到设备 {device.Code} 失败：{connect.Message}");
                        }
                    }

                    var data = new Dictionary<string, object>
                    {
                        { "TimeStamp", DateTime.Now },
                        { "Device", device.Code }
                    };

                    foreach (var metricColumnConfig in metricTableConfig.MetricColumnConfigs)
                    {
                        try
                        {
                            data[metricColumnConfig.ColumnName] = await ParseValue(plcClient, metricColumnConfig.DataAddress, metricColumnConfig.DataLength, metricColumnConfig.DataType);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - 读取设备 {device.Code} 失败：{ex.Message}");
                        }
                    }
                    
                    dataStorage.Save(data, metricTableConfig);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - 采集数据异常: {ex.Message}");
                }
                await Task.Delay(metricTableConfig.CollectionFrequency, _cts.Token);
            }
        }, TaskCreationOptions.LongRunning);
        
        var taskKey = GenerateTaskKey(device, metricTableConfig);
        _runningTasks[taskKey] = task;
    }
    
    /// <summary>
    /// 读取数据
    /// </summary>
    /// <param name="plcClient"></param>
    /// <param name="dataAddress"></param>
    /// <param name="dataLength"></param>
    /// <param name="dataType"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    private async Task<object> ParseValue(IPLCClient plcClient, string dataAddress, ushort dataLength, string dataType)
    {
        return dataType.ToLower() switch
        {
            "int" => (await RetryOnFailure(() => plcClient.ReadInt32Async(dataAddress))).Content,
            "float" => (await RetryOnFailure(() => plcClient.ReadFloatAsync(dataAddress))).Content,
            "double" => (await RetryOnFailure(() => plcClient.ReadDoubleAsync(dataAddress))).Content,
            "string" => (await RetryOnFailure(() => plcClient.ReadStringAsync(dataAddress, dataLength))).Content,
            "boolean" => (await RetryOnFailure(() => plcClient.ReadBoolAsync(dataAddress))).Content,
            _ => throw new ArgumentException("未知的数据类型")
        };
    }
    
    
    /// <summary>
    /// 失败重试读取
    /// </summary>
    /// <param name="action"></param>
    /// <param name="maxRetries"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task<OperationResult<T>> RetryOnFailure<T>(Func<Task<OperationResult<T>>> action, int maxRetries = 3)
    {
        var retries = 0;
        while (retries < maxRetries)
        {
            var result = await action();
            if (result.IsSuccess)
            {
                return result;
            }
            retries++;
            await Task.Delay(1000);  // 等待1秒后重试
        }
        throw new Exception($"操作失败，已达到最大重试次数 {maxRetries}。");
    }
    
    /// <summary>
    /// 监听退出事件
    /// </summary>
    public void ListenExitEvents()
    {
        Console.CancelKeyPress += async (sender, e) =>
        {
            e.Cancel = true; 
            await HandleExitAsync();
        };
        AppDomain.CurrentDomain.ProcessExit += async (s, e) => await HandleExitAsync();
    }

    /// <summary>
    /// 处理退出
    /// </summary>
    private async Task HandleExitAsync()
    {
        _cts.Cancel();

        foreach (var plcClient in _plcClients)
        {
            await plcClient.ConnectCloseAsync();
        }
        
        foreach (var dataStorage in _dataStorages)
        {
            dataStorage.Release();
        }
        
        LogExitInformation("程序已正常退出");
    }

    /// <summary>
    /// 打印退出日志文件
    /// </summary>
    /// <param name="message"></param>
    private void LogExitInformation(string message)
    {
        var logFilePath = Path.Combine(AppContext.BaseDirectory, "exit_log.txt");
        var logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}\n";
        File.AppendAllText(logFilePath, logMessage);
    }
}
