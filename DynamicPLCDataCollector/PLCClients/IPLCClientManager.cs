﻿using DynamicPLCDataCollector.Models;

namespace DynamicPLCDataCollector.PLCClients;

/// <summary>
/// PLC 通讯类管理类器定义
/// </summary>
public interface IPLCClientManager
{
    /// <summary>
    /// 读取数据
    /// </summary>
    /// <param name="device"></param>
    /// <param name="metricTableConfig"></param>
    /// <returns></returns>
    Task<Dictionary<string, object>> ReadAsync(Device device, MetricTableConfig metricTableConfig);
    
    /// <summary>
    /// 释放连接
    /// </summary>
    /// <returns></returns>
    Task DisconnectAllAsync();
}