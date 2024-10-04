configs = \
{
    "dup":
    {
    },

    "svr":
    {
        "DOTNET_GCDynamicAdaptationMode": 0,
    },

    "svr4":
    {
        "DOTNET_GCMaxHeapCount": 4,
        "DOTNET_GCDynamicAdaptationMode": 0,
    },

    "h4":
    {
        "DOTNET_GCMaxHeapCount": 4,
    },
    
    "mult8":
    {
        "DOTNET_GCMaxHeapCount": 4,
        "DOTNET_GCDynamicAdaptationMult": 8,
    },

    "mult32":
    {
        "DOTNET_GCMaxHeapCount": 4,
        "DOTNET_GCDynamicAdaptationMult": 32,
    },

    "x10":
    {
        "DOTNET_GCMaxHeapCount": 4,
        "DOTNET_GCDynamicAdaptationCalcMax": 10,
    },

    "mult8x10":
    {
        "DOTNET_GCMaxHeapCount": 4,
        "DOTNET_GCDynamicAdaptationMult": 8,
        "DOTNET_GCDynamicAdaptationCalcMax": 10,
    },

    "mult32x10":
    {
        "DOTNET_GCMaxHeapCount": 4,
        "DOTNET_GCDynamicAdaptationMult": 32,
        "DOTNET_GCDynamicAdaptationCalcMax": 10,
    },

    "log":
    {
        "DOTNET_GCLogEnabled": 1,
        "DOTNET_GCLogFile": ".\gclog",
        "DOTNET_GCLogFileSize": 10
    },

}
