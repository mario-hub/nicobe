#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <dcomp.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>
using Microsoft::WRL::ComPtr;
static void Check(HRESULT hr) { if(FAILED(hr)) throw hr; }
static double Now() { static double f=[] { LARGE_INTEGER n;QueryPerformanceFrequency(&n);return double(n.QuadPart); }();LARGE_INTEGER n;QueryPerformanceCounter(&n);return n.QuadPart/f; }
struct Item { int id;float x,y,size;unsigned color;float velocity,remaining;const wchar_t* text; };
struct Sprite { int id;float x,y,size;unsigned color;float velocity,remaining;std::wstring text; };
struct Stats { double fps,cpuMs,cacheMs;int visible,pending;unsigned error;int hardware;wchar_t adapter[128]; };
struct Bench { double gpuMeanMs,gpuP95Ms,cpuMeanMs,coldMs,pacedFps;int count,width,height,frames;wchar_t adapter[128]; };
struct ComScope { HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);~ComScope(){if(SUCCEEDED(hr))CoUninitialize();} };

// DirectWrite performs font fallback/shaping; the outline and fill are both
// rasterized by Direct2D on the hardware device, once per cached comment.
class Outline final : public IDWriteTextRenderer {
    ID2D1DeviceContext* dc;ID2D1Factory1* factory;ID2D1Brush* fill;ID2D1Brush* edge;
public:
    Outline(ID2D1DeviceContext* d,ID2D1Factory1* f,ID2D1Brush* b,ID2D1Brush* e):dc(d),factory(f),fill(b),edge(e){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** p) override {if(!p)return E_POINTER;*p=nullptr;if(iid==__uuidof(IUnknown)||iid==__uuidof(IDWriteTextRenderer)||iid==__uuidof(IDWritePixelSnapping)){*p=this;return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return 1;} ULONG STDMETHODCALLTYPE Release() override{return 1;}
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*,BOOL* p) override{*p=TRUE;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*,DWRITE_MATRIX* p) override{*p={1,0,0,1,0,0};return S_OK;}
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*,FLOAT* p) override{*p=1;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*,FLOAT x,FLOAT y,DWRITE_MEASURING_MODE,const DWRITE_GLYPH_RUN* run,const DWRITE_GLYPH_RUN_DESCRIPTION*,IUnknown*) override {
        try {ComPtr<ID2D1PathGeometry> path;Check(factory->CreatePathGeometry(&path));ComPtr<ID2D1GeometrySink> sink;Check(path->Open(&sink));
            Check(run->fontFace->GetGlyphRunOutline(run->fontEmSize,run->glyphIndices,run->glyphAdvances,run->glyphOffsets,run->glyphCount,run->isSideways,run->bidiLevel%2,sink.Get()));Check(sink->Close());
            D2D1_MATRIX_3X2_F old;dc->GetTransform(&old);dc->SetTransform(D2D1::Matrix3x2F::Translation(x,y)*old);
            dc->DrawGeometry(path.Get(),edge,2.5f);dc->FillGeometry(path.Get(),fill);dc->SetTransform(old);return S_OK;
        } catch(HRESULT hr){return hr;}
    }
    HRESULT STDMETHODCALLTYPE DrawUnderline(void*,FLOAT,FLOAT,const DWRITE_UNDERLINE*,IUnknown*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*,FLOAT,FLOAT,const DWRITE_STRIKETHROUGH*,IUnknown*) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawInlineObject(void*,FLOAT,FLOAT,IDWriteInlineObject*,BOOL,BOOL,IUnknown*) override{return S_OK;}
};
struct Tile { ComPtr<ID2D1Bitmap1> bitmap;float x,y,w,h; };
struct Glyph {std::vector<Tile> tiles;size_t bytes=0;unsigned long long used=0;};
class Graphics {
public:
    ComPtr<ID3D11Device> d3d;ComPtr<ID3D11DeviceContext> immediate;
    ComPtr<IDXGIDevice> dxgi;ComPtr<IDXGIFactory2> dxFactory;
    ComPtr<ID2D1Factory1> factory;ComPtr<ID2D1Device> device;ComPtr<ID2D1DeviceContext> dc;ComPtr<IDWriteFactory> write;
    ComPtr<IDXGISwapChain1> swap;ComPtr<ID2D1Bitmap1> target;ComPtr<ID3D11Texture2D> offscreen;
    ComPtr<IDCompositionDevice> composition;ComPtr<IDCompositionTarget> compositionTarget;ComPtr<IDCompositionVisual> root;
    std::unordered_map<std::wstring,Glyph> cache;std::unordered_map<int,std::wstring> keys;size_t cacheBytes=0;
    std::wstring font=L"Yu Gothic UI",adapter;unsigned long long frame=0;UINT width=0,height=0;float opacity=.95f;
    ~Graphics() {
        if(compositionTarget)compositionTarget->SetRoot(nullptr);
        if(root)root->SetContent(nullptr);
        if(composition){composition->Commit();composition->WaitForCommitCompletion();}
        if(dc)dc->SetTarget(nullptr);
        cache.clear();keys.clear();target.Reset();offscreen.Reset();swap.Reset();
        if(immediate){immediate->ClearState();immediate->Flush();}
    }
    void Init() {
        D3D_FEATURE_LEVEL level;
        Check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&d3d,&level,&immediate));
        Check(d3d.As(&dxgi));ComPtr<IDXGIAdapter> ad;Check(dxgi->GetAdapter(&ad));DXGI_ADAPTER_DESC desc{};Check(ad->GetDesc(&desc));adapter=desc.Description;
        Check(ad->GetParent(IID_PPV_ARGS(&dxFactory)));
        D2D1_FACTORY_OPTIONS opts{};Check(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,opts,factory.GetAddressOf()));
        Check(factory->CreateDevice(dxgi.Get(),&device));Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&dc));
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(write.GetAddressOf())));
        dc->SetDpi(96,96);dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    }
    void Attach(HWND hwnd) {
        DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=1;desc.Height=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
        desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;desc.Scaling=DXGI_SCALING_STRETCH;
        desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;
        Check(dxFactory->CreateSwapChainForComposition(d3d.Get(),&desc,nullptr,&swap));
        ComPtr<IDXGIDevice1> latency;Check(dxgi.As(&latency));Check(latency->SetMaximumFrameLatency(1));
        Check(DCompositionCreateDevice(dxgi.Get(),IID_PPV_ARGS(&composition)));Check(composition->CreateTargetForHwnd(hwnd,TRUE,&compositionTarget));
        Check(composition->CreateVisual(&root));Check(root->SetContent(swap.Get()));Check(compositionTarget->SetRoot(root.Get()));Check(composition->Commit());
    }
    void Resize(UINT w,UINT h) {
        if(width==w && height==h)return;
        dc->SetTarget(nullptr);target.Reset();offscreen.Reset();
        ComPtr<IDXGISurface> surface;
        if(swap) {Check(swap->ResizeBuffers(0,w,h,DXGI_FORMAT_UNKNOWN,0));Check(swap->GetBuffer(0,IID_PPV_ARGS(&surface)));}
        else {D3D11_TEXTURE2D_DESC t{};t.Width=w;t.Height=h;t.MipLevels=1;t.ArraySize=1;t.Format=DXGI_FORMAT_B8G8R8A8_UNORM;t.SampleDesc.Count=1;t.Usage=D3D11_USAGE_DEFAULT;t.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;Check(d3d->CreateTexture2D(&t,nullptr,&offscreen));Check(offscreen.As(&surface));}
        auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED));
        Check(dc->CreateBitmapFromDxgiSurface(surface.Get(),&props,&target));dc->SetTarget(target.Get());width=w;height=h;
    }
    std::wstring Key(const Sprite& s) {return std::to_wstring(s.size)+L"/"+std::to_wstring(s.color)+L"/"+s.text;}
    Glyph Build(const Sprite& s) {
        Glyph result;ComPtr<IDWriteTextFormat> format;Check(write->CreateTextFormat(font.c_str(),nullptr,DWRITE_FONT_WEIGHT_BOLD,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,s.size,L"ja-JP",&format));
        Check(format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP));ComPtr<IDWriteTextLayout> layout;
        Check(write->CreateTextLayout(s.text.c_str(),static_cast<UINT32>(s.text.size()),format.Get(),100000,100000,&layout));
        DWRITE_TEXT_METRICS metrics{};DWRITE_OVERHANG_METRICS over{};Check(layout->GetMetrics(&metrics));Check(layout->GetOverhangMetrics(&over));
        float padX=3+std::max(0.f,over.left),padY=3+std::max(0.f,over.top);
        UINT w=static_cast<UINT>(std::ceil(metrics.widthIncludingTrailingWhitespace+padX+3+std::max(0.f,over.right)));
        UINT h=static_cast<UINT>(std::ceil(metrics.height+padY+3+std::max(0.f,over.bottom)));
        // Bound total texture storage for pathological multi-line comments.
        if(!w||!h||uint64_t(w)*h>16*1024*1024)throw E_INVALIDARG;
        ComPtr<ID2D1SolidColorBrush> fill,edge;
        Check(dc->CreateSolidColorBrush(D2D1::ColorF((s.color>>16&255)/255.f,(s.color>>8&255)/255.f,(s.color&255)/255.f),&fill));
        Check(dc->CreateSolidColorBrush(D2D1::ColorF((s.color&0xffffff)==0?D2D1::ColorF::White:D2D1::ColorF::Black),&edge));
        Outline renderer(dc.Get(),factory.Get(),fill.Get(),edge.Get());
        auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED));
        for(UINT y=0;y<h;y+=2048)for(UINT x=0;x<w;x+=2048) {
            UINT tw=std::min(2048u,w-x),th=std::min(2048u,h-y);Tile tile;tile.x=x-padX;tile.y=y-padY;tile.w=float(tw);tile.h=float(th);
            Check(dc->CreateBitmap(D2D1::SizeU(tw,th),nullptr,0,&props,&tile.bitmap));dc->SetTarget(tile.bitmap.Get());dc->BeginDraw();dc->SetTransform(D2D1::Matrix3x2F::Identity());dc->Clear(D2D1::ColorF(0,0));
            HRESULT textResult=layout->Draw(nullptr,&renderer,padX-x,padY-y);HRESULT endResult=dc->EndDraw();dc->SetTarget(target.Get());Check(textResult);Check(endResult);
            result.bytes+=size_t(tw)*th*4;result.tiles.push_back(std::move(tile));
        }
        return result;
    }
    int Prepare(const std::vector<Sprite>& scene,double budgetMs) {
        double start=Now();int pending=0;std::unordered_set<int> live;live.reserve(scene.size());
        for(const auto& s:scene) {
            live.insert(s.id);auto id=keys.find(s.id);if(id==keys.end())id=keys.emplace(s.id,Key(s)).first;
            auto glyph=cache.find(id->second);
            if(glyph==cache.end()) {
                if((Now()-start)*1000>=budgetMs){pending++;continue;}
                try {auto built=Build(s);cacheBytes+=built.bytes;glyph=cache.emplace(id->second,std::move(built)).first;}
                catch(HRESULT hr){if(hr!=E_INVALIDARG)throw;glyph=cache.emplace(id->second,Glyph{}).first;}
            }
            glyph->second.used=frame;
        }
        for(auto i=keys.begin();i!=keys.end();)if(!live.count(i->first))i=keys.erase(i);else ++i;
        while(cacheBytes>64*1024*1024 || cache.size()>512) {
            auto oldest=cache.end();for(auto it=cache.begin();it!=cache.end();++it)if(it->second.used!=frame && (oldest==cache.end() || it->second.used<oldest->second.used))oldest=it;
            if(oldest==cache.end())break;cacheBytes-=oldest->second.bytes;cache.erase(oldest);
        }
        return pending;
    }
    void Draw(const std::vector<Sprite>& scene,double elapsed,bool moving,bool background=false) {
        dc->SetTarget(target.Get());dc->BeginDraw();dc->SetTransform(D2D1::Matrix3x2F::Scale(width/1920.f,height/1080.f));
        dc->Clear(background?D2D1::ColorF(.5f,.59f,.54f,1):D2D1::ColorF(0,0));
        for(const auto& s:scene) {
            auto key=keys.find(s.id);if(key==keys.end())continue;auto found=cache.find(key->second);if(found==cache.end())continue;
            float dt=moving?float(elapsed):0;if(s.remaining>=0 && dt>=s.remaining)continue;
            float x=s.x-s.velocity*dt;
            for(const auto& t:found->second.tiles) {
                float left=x+t.x,top=s.y+t.y;if(left>1920 || left+t.w<0 || top>1080 || top+t.h<0)continue;
                dc->DrawBitmap(t.bitmap.Get(),D2D1::RectF(left,top,left+t.w,top+t.h),opacity,D2D1_INTERPOLATION_MODE_LINEAR);
            }
        }
        Check(dc->EndDraw());
    }
    void SavePng(const wchar_t* path) {
        D3D11_TEXTURE2D_DESC desc{};offscreen->GetDesc(&desc);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> read;Check(d3d->CreateTexture2D(&desc,nullptr,&read));immediate->CopyResource(read.Get(),offscreen.Get());
        D3D11_MAPPED_SUBRESOURCE map{};Check(immediate->Map(read.Get(),0,D3D11_MAP_READ,0,&map));
        try {
            ComPtr<IWICImagingFactory> wic;Check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic)));
            ComPtr<IWICStream> stream;Check(wic->CreateStream(&stream));Check(stream->InitializeFromFilename(path,GENERIC_WRITE));
            ComPtr<IWICBitmapEncoder> encoder;Check(wic->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder));Check(encoder->Initialize(stream.Get(),WICBitmapEncoderNoCache));
            ComPtr<IWICBitmapFrameEncode> image;Check(encoder->CreateNewFrame(&image,nullptr));Check(image->Initialize(nullptr));Check(image->SetSize(width,height));WICPixelFormatGUID fmt=GUID_WICPixelFormat32bppBGRA;Check(image->SetPixelFormat(&fmt));
            Check(image->WritePixels(height,map.RowPitch,map.RowPitch*height,static_cast<BYTE*>(map.pData)));Check(image->Commit());Check(encoder->Commit());
        } catch(...){immediate->Unmap(read.Get(),0);throw;}immediate->Unmap(read.Get(),0);
    }
    struct Signature {double center=0;uint64_t alpha=0;int transparent=0,white=0,black=0;};
    Signature Inspect() {
        D3D11_TEXTURE2D_DESC desc{};offscreen->GetDesc(&desc);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> read;Check(d3d->CreateTexture2D(&desc,nullptr,&read));immediate->CopyResource(read.Get(),offscreen.Get());D3D11_MAPPED_SUBRESOURCE map{};Check(immediate->Map(read.Get(),0,D3D11_MAP_READ,0,&map));
        Signature s;double moment=0;
        for(UINT y=0;y<height;y++) {auto p=static_cast<BYTE*>(map.pData)+y*map.RowPitch;for(UINT x=0;x<width;x++,p+=4){s.alpha+=p[3];moment+=double(x)*p[3];if(!p[3])s.transparent++;if(p[3]>250){if(p[0]>250&&p[1]>250&&p[2]>250)s.white++;if(p[0]<5&&p[1]<5&&p[2]<5)s.black++;}}}
        immediate->Unmap(read.Get(),0);s.center=s.alpha?moment/s.alpha:0;return s;
    }
};
class Timer {
    HANDLE timer=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
public:
    Timer(){if(!timer)timer=CreateWaitableTimerW(nullptr,FALSE,nullptr);if(!timer)throw HRESULT_FROM_WIN32(GetLastError());}
    ~Timer(){CloseHandle(timer);}
    void Until(double deadline){double left=deadline-Now();if(left<=0)return;LARGE_INTEGER due;due.QuadPart=-std::max<LONGLONG>(1,static_cast<LONGLONG>(left*10000000));if(SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE))WaitForSingleObject(timer,100);}
};
static LRESULT CALLBACK WindowProc(HWND hwnd,UINT message,WPARAM w,LPARAM l) {
    if(message==WM_NCHITTEST)return HTTRANSPARENT;if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
    if(message==WM_ERASEBKGND)return 1;if(message==WM_PAINT){PAINTSTRUCT p;BeginPaint(hwnd,&p);EndPaint(hwnd,&p);return 0;}
    return DefWindowProcW(hwnd,message,w,l);
}
class Renderer {
public:
    inline static std::atomic<int> active=0;
    std::mutex gate;std::condition_variable initialized;std::atomic<bool> stop=false;bool ready=false;HRESULT initError=S_OK;
    HWND player=nullptr;bool enabled=false,moving=false;double stamp=0,lease=0;std::vector<Sprite> scene;Stats stats{};
    std::wstring font;float opacity;std::thread thread;
    Renderer(const wchar_t* family,float alpha):font(family),opacity(alpha) {thread=std::thread([this]{Run();});std::unique_lock lock(gate);initialized.wait(lock,[this]{return ready;});active++;}
    ~Renderer(){stop=true;if(thread.joinable())thread.join();active--;}
    void Run() {
        ComScope com;HWND window=nullptr;
        try {
            Graphics g;g.font=font;g.opacity=opacity;g.Init();
            WNDCLASSW wc{};wc.lpfnWndProc=WindowProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"NicoBEGpuOverlay";RegisterClassW(&wc);
            window=CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP|WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,wc.lpszClassName,L"NicoBE GPU コメント",WS_POPUP,0,0,1,1,nullptr,nullptr,wc.hInstance,nullptr);
            if(!window)throw HRESULT_FROM_WIN32(GetLastError());Check(SetLayeredWindowAttributes(window,0,255,LWA_ALPHA)?S_OK:HRESULT_FROM_WIN32(GetLastError()));g.Attach(window);
            {std::lock_guard lock(gate);stats.hardware=1;wcsncpy_s(stats.adapter,g.adapter.c_str(),_TRUNCATE);ready=true;initialized.notify_one();}
            Timer timer;double deadline=Now(),sampleStart=deadline;int frames=0;double cpu=0,cacheTime=0;HWND owner=nullptr;RECT previous{};
            while(!stop) {
                MSG message;while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
                HWND host;bool show,animate;double timestamp,alive;std::vector<Sprite> items;
                {std::lock_guard lock(gate);host=player;show=enabled;animate=moving;timestamp=stamp;alive=lease;items=scene;}
                double now=Now();RECT rect{};POINT point{};HWND video=host?GetDlgItem(host,59648):nullptr;
                show=show && now-timestamp<2 && host && IsWindow(host) && IsWindowVisible(host) && !IsIconic(host) && video && GetClientRect(video,&rect) && rect.right>20 && rect.bottom>20 && ClientToScreen(video,&point);
                int pending=0;
                if(show) {
                    rect.left=point.x;rect.top=point.y;rect.right+=point.x;rect.bottom+=point.y;
                    if(owner!=host){SetWindowLongPtrW(window,GWLP_HWNDPARENT,reinterpret_cast<LONG_PTR>(host));owner=host;}
                    bool first=!IsWindowVisible(window),moved=!EqualRect(&previous,&rect);
                    bool raise=GetForegroundWindow()==host && GetWindow(host,GW_HWNDPREV)!=window;
                    if(first||moved||raise) {
                        UINT flags=SWP_NOACTIVATE|SWP_NOOWNERZORDER|SWP_SHOWWINDOW;
                        // A tray click leaves focus outside the player. Restore the
                        // overlay just above its owner even without foreground focus,
                        // while leaving unrelated foreground windows above both.
                        HWND after=GetWindow(host,GW_HWNDPREV);
                        if((!first&&!raise)||after==window)flags|=SWP_NOZORDER;
                        SetWindowPos(window,after?after:HWND_TOP,rect.left,rect.top,rect.right-rect.left,rect.bottom-rect.top,flags);previous=rect;
                    }
                    g.Resize(rect.right-rect.left,rect.bottom-rect.top);g.frame++;
                    double began=Now();pending=g.Prepare(items,3.0);cacheTime+=(Now()-began)*1000;
                    g.Draw(items,std::min(std::max(0.,now-timestamp),alive),animate);
                    // The compositor synchronizes presentation to the desktop. The
                    // high-resolution timer caps submissions at 60 Hz even on 144 Hz displays.
                    Check(g.swap->Present(0,0));cpu+=(Now()-began)*1000;frames++;
                } else if(IsWindowVisible(window))ShowWindow(window,SW_HIDE);
                now=Now();if(now-sampleStart>=1) {
                    std::lock_guard lock(gate);stats.fps=frames/(now-sampleStart);stats.cpuMs=frames?cpu/frames:0;stats.cacheMs=frames?cacheTime/frames:0;stats.visible=static_cast<int>(items.size());stats.pending=pending;
                    frames=0;cpu=cacheTime=0;sampleStart=now;
                }
                deadline+=1./60;if(deadline<now)deadline=now;timer.Until(deadline);
            }
        } catch(HRESULT hr) {std::lock_guard lock(gate);stats.error=static_cast<unsigned>(hr);initError=hr;ready=true;initialized.notify_one();}
          catch(...) {std::lock_guard lock(gate);stats.error=static_cast<unsigned>(E_FAIL);initError=E_FAIL;ready=true;initialized.notify_one();}
        if(window)DestroyWindow(window);
    }
};
#define API extern "C" __declspec(dllexport)
API HRESULT __cdecl NicoCreate(const wchar_t* font,float opacity,Renderer** output) {
    if(!font||!output)return E_INVALIDARG;*output=nullptr;
    try {auto r=new Renderer(font,opacity);if(FAILED(r->initError)){HRESULT hr=r->initError;delete r;return hr;}*output=r;return S_OK;}catch(...){return E_FAIL;}
}
API void __cdecl NicoDestroy(Renderer* r){delete r;}
API int __cdecl NicoActiveRenderers(){return Renderer::active.load();}
API void __cdecl NicoFollow(Renderer* r,HWND player,int enabled){if(!r)return;std::lock_guard lock(r->gate);r->player=player;r->enabled=enabled!=0;}
API void __cdecl NicoSubmit(Renderer* r,const Item* items,int count,int moving,double lease) {
    if(!r||count<0||count>300||(!items&&count))return;
    std::vector<Sprite> scene;scene.reserve(count);for(int i=0;i<count;i++){auto& s=items[i];scene.push_back({s.id,s.x,s.y,s.size,s.color,s.velocity,s.remaining,s.text?s.text:L""});}
    std::lock_guard lock(r->gate);r->scene=std::move(scene);r->stamp=Now();r->moving=moving!=0;r->lease=std::clamp(lease,0.,.65);
}
API void __cdecl NicoStats(Renderer* r,Stats* stats){if(!r||!stats)return;std::lock_guard lock(r->gate);*stats=r->stats;}
API HRESULT __cdecl NicoValidate(int* checks) {
    if(!checks)return E_POINTER;*checks=0;ComScope com;
    try {
        Graphics g;g.Init();g.Resize(1920,1080);g.opacity=1;std::vector<Sprite> items{{1,500,100,32,0xffffff,240,-1,L"ここすき 神が怒った"}};
        g.frame++;g.Prepare(items,10000);g.Draw(items,0,true);auto original=g.Inspect();
        if(original.transparent<1900000||original.white==0||original.black==0)throw E_FAIL;(*checks)++;
        g.Draw(items,1,true);auto moved=g.Inspect();if(std::abs(original.center-moved.center-240)>.1)throw E_FAIL;(*checks)++;
        g.Draw(items,1,false);auto paused=g.Inspect();if(paused.alpha!=original.alpha||std::abs(paused.center-original.center)>.01)throw E_FAIL;(*checks)++;
        items[0].x=700;g.Draw(items,0,true);auto seek=g.Inspect();if(std::abs(seek.center-original.center-200)>.1)throw E_FAIL;(*checks)++;
        items[0].velocity=0;items[0].remaining=.5f;g.Draw(items,1,true);if(g.Inspect().alpha!=0)throw E_FAIL;(*checks)++;
        g.Draw(items,1,false);if(g.Inspect().alpha==0)throw E_FAIL;(*checks)++;
        items.clear();g.Draw(items,0,false);if(g.Inspect().alpha!=0)throw E_FAIL;(*checks)++;
        g.Resize(1280,720);items.push_back({2,500,100,32,0xffffff,240,-1,L"ここすき 神が怒った"});g.frame++;g.Prepare(items,10000);g.Draw(items,0,true);auto resized=g.Inspect();if(std::abs(resized.center-original.center*2/3)>1)throw E_FAIL;(*checks)++;
        return S_OK;
    }catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}
}
API HRESULT __cdecl NicoBEnchmark(int count,int width,int height,Bench* result,const wchar_t* png) {
    if(!result||count<1||count>300||width<1||height<1)return E_INVALIDARG;
    ComScope com;
    try {
        Graphics g;g.Init();g.Resize(width,height);g.opacity=1;std::vector<Sprite> items;
        for(int i=0;i<count;i++)items.push_back({i,float((i/20)*400),float((i%20)*52),32, i%3==0?0xffffffu:i%3==1?0xff8080u:0u,240,-1,L"GPU 描画テスト ここすき 神が怒った "+std::to_wstring(i)});
        double cold=Now();g.frame++;g.Prepare(items,100000);cold=(Now()-cold)*1000;
        ComPtr<ID3D11Query> disjoint,start,end;D3D11_QUERY_DESC q{D3D11_QUERY_TIMESTAMP_DISJOINT,0};Check(g.d3d->CreateQuery(&q,&disjoint));q.Query=D3D11_QUERY_TIMESTAMP;Check(g.d3d->CreateQuery(&q,&start));Check(g.d3d->CreateQuery(&q,&end));
        std::vector<double> times;double cpu=0;Timer timer;double began=Now(),deadline=began;
        for(int frame=0;frame<180;frame++) {
            double a=Now();g.immediate->Begin(disjoint.Get());g.immediate->End(start.Get());g.Draw(items,(frame%60)/60.,true,true);g.immediate->End(end.Get());g.immediate->End(disjoint.Get());g.immediate->Flush();cpu+=(Now()-a)*1000;
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT data{};HRESULT hr;
            double timeout=Now()+5;while((hr=g.immediate->GetData(disjoint.Get(),&data,sizeof(data),0))==S_FALSE){if(Now()>timeout)throw HRESULT_FROM_WIN32(WAIT_TIMEOUT);Sleep(0);}Check(hr);
            UINT64 s=0,e=0;Check(g.immediate->GetData(start.Get(),&s,sizeof(s),0));Check(g.immediate->GetData(end.Get(),&e,sizeof(e),0));
            if(!data.Disjoint)times.push_back(1000.*(e-s)/data.Frequency);
            deadline+=1./60;timer.Until(deadline);
        }
        double duration=Now()-began;if(times.empty())throw E_FAIL;std::sort(times.begin(),times.end());double sum=0;for(double t:times)sum+=t;
        *result={sum/times.size(),times[static_cast<size_t>((times.size()-1)*.95)],cpu/180,cold,180/duration,count,width,height,180,{}};wcsncpy_s(result->adapter,g.adapter.c_str(),_TRUNCATE);
        if(png&&*png)g.SavePng(png);return S_OK;
    }catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}
}
