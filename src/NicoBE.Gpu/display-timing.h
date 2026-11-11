#pragma once

// Rational rates preserve modes such as 60000/1001 Hz. Wait on the chosen
// output, rather than the swap chain (whose clock may follow the primary).
struct RefreshRate {
    UINT numerator=0,denominator=0;
    bool Known() const {return denominator && numerator>denominator;}
    double Hz() const {return Known()?double(numerator)/denominator:0.;}
    UINT Divisor() const {return Known()?std::max(1U,UINT(numerator/(60ULL*denominator))):1;}
    double Target() const {return Known()?Hz()/Divisor():60.;}
};

static RefreshRate ReadRefreshRate(HMONITOR handle) {
    MONITORINFOEXW monitor{};monitor.cbSize=sizeof(monitor);
    if(!GetMonitorInfoW(handle,&monitor))return {};
    UINT flags=QDC_ONLY_ACTIVE_PATHS|QDC_VIRTUAL_MODE_AWARE|QDC_VIRTUAL_REFRESH_RATE_AWARE;
    for(int attempt=0;attempt<3;attempt++) {
        UINT pathsCount=0,modesCount=0;
        LONG result=GetDisplayConfigBufferSizes(flags,&pathsCount,&modesCount);
        if(result==ERROR_INVALID_PARAMETER){flags=QDC_ONLY_ACTIVE_PATHS;continue;}
        if(result!=ERROR_SUCCESS)break;
        std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathsCount);
        std::vector<DISPLAYCONFIG_MODE_INFO> modes(modesCount);
        result=QueryDisplayConfig(flags,&pathsCount,paths.data(),&modesCount,modes.data(),nullptr);
        if(result==ERROR_INSUFFICIENT_BUFFER)continue;
        if(result==ERROR_INVALID_PARAMETER){flags=QDC_ONLY_ACTIVE_PATHS;continue;}
        if(result!=ERROR_SUCCESS)break;
        RefreshRate found;
        for(UINT i=0;i<pathsCount;i++) {
            const auto& path=paths[i];DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
            source.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;source.header.size=sizeof(source);
            source.header.adapterId=path.sourceInfo.adapterId;source.header.id=path.sourceInfo.id;
            if(DisplayConfigGetDeviceInfo(&source.header)!=ERROR_SUCCESS || _wcsicmp(source.viewGdiDeviceName,monitor.szDevice)!=0)continue;
            RefreshRate rate{path.targetInfo.refreshRate.Numerator,path.targetInfo.refreshRate.Denominator};
            // Mirrored outputs share a GDI source. Use the slowest active target.
            if(rate.Known() && (!found.Known() || rate.Hz()<found.Hz()))found=rate;
        }
        if(found.Known())return found;
        break;
    }
    DEVMODEW mode{};mode.dmSize=sizeof(mode);
    if(EnumDisplaySettingsW(monitor.szDevice,ENUM_CURRENT_SETTINGS,&mode) && mode.dmDisplayFrequency>1)return {mode.dmDisplayFrequency,1};
    return {};
}

struct DisplaySource {
    RefreshRate rate;
    Microsoft::WRL::ComPtr<IDXGIOutput> output;
};
static DisplaySource ReadDisplaySource(HMONITOR monitor) {
    DisplaySource result;result.rate=ReadRefreshRate(monitor);
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    if(FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))return result;
    for(UINT ai=0;;ai++) {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        if(FAILED(factory->EnumAdapters1(ai,&adapter)))break;
        for(UINT oi=0;;oi++) {
            Microsoft::WRL::ComPtr<IDXGIOutput> output;
            if(FAILED(adapter->EnumOutputs(oi,&output)))break;
            DXGI_OUTPUT_DESC desc{};
            if(SUCCEEDED(output->GetDesc(&desc))&&desc.AttachedToDesktop&&desc.Monitor==monitor) {
                result.output=output;return result;
            }
        }
    }
    return result;
}

struct DisplayTiming {
    bool dirty=true;
    unsigned revision=0;
    HMONITOR monitor=nullptr;
    double nextCheck=0;
    RefreshRate rate;
    void Invalidate(){dirty=true;revision++;}
    bool CanApply(HMONITOR requested,unsigned requestRevision,HMONITOR current) const {
        return requested==current && requestRevision==revision;
    }
    bool NeedsRefresh(bool visible,bool first,HMONITOR current,double now) const {
        return visible && (first || dirty || current!=monitor || now>=nextCheck);
    }
    void Updated(HMONITOR current,double now,RefreshRate value) {
        monitor=current;rate=value;dirty=false;nextCheck=now+5;
    }
};

