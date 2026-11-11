#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <d2d1_3.h>
#include <dwrite_3.h>
#include <dcomp.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <future>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include "display-timing.h"
using Microsoft::WRL::ComPtr;
static void Check(HRESULT hr) { if(FAILED(hr)) throw hr; }
static double Now() { static double f=[] { LARGE_INTEGER n;QueryPerformanceFrequency(&n);return double(n.QuadPart); }();LARGE_INTEGER n;QueryPerformanceCounter(&n);return n.QuadPart/f; }
struct Item { int id;float x,y,size;unsigned color;float velocity,remaining;const wchar_t* text; };
struct Sprite { int id;float x,y,size;unsigned color;float velocity,remaining;std::wstring text; };
struct Stats { double fps,cpuMs,cacheMs;int visible,pending;unsigned error;int hardware;wchar_t adapter[128]; };
struct DisplayStats {double refreshHz,targetFps;unsigned divisor;};
struct PacingStats {double meanMs,p95Ms,maxMs;unsigned busy;int outputSync;};
struct Bench { double gpuMeanMs,gpuP95Ms,cpuMeanMs,coldMs,pacedFps;int count,width,height,frames;wchar_t adapter[128]; };
struct ComScope { HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);~ComScope(){if(SUCCEEDED(hr))CoUninitialize();} };

// DirectWrite performs font fallback/shaping; the outline and fill are both
// rasterized by Direct2D on the hardware device, once per cached comment.
class Outline final : public IDWriteTextRenderer {
    ID2D1DeviceContext* dc;ID2D1Factory1* factory;ID2D1Brush* fill;ID2D1Brush* edge;
    IDWriteFactory4* colorWrite;ID2D1DeviceContext4* colorDc;float width;bool shadowOnly;
public:
    Outline(ID2D1DeviceContext* d,ID2D1Factory1* f,ID2D1Brush* b,ID2D1Brush* e,IDWriteFactory4* w,ID2D1DeviceContext4* c,float stroke,bool shadow=false):dc(d),factory(f),fill(b),edge(e),colorWrite(w),colorDc(c),width(stroke),shadowOnly(shadow){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** p) override {if(!p)return E_POINTER;*p=nullptr;if(iid==__uuidof(IUnknown)||iid==__uuidof(IDWriteTextRenderer)||iid==__uuidof(IDWritePixelSnapping)){*p=this;return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return 1;} ULONG STDMETHODCALLTYPE Release() override{return 1;}
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*,BOOL* p) override{*p=TRUE;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*,DWRITE_MATRIX* p) override{*p={1,0,0,1,0,0};return S_OK;}
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*,FLOAT* p) override{*p=1;return S_OK;}
    HRESULT Monochrome(FLOAT x,FLOAT y,const DWRITE_GLYPH_RUN* run) {
        try {ComPtr<ID2D1PathGeometry> path;Check(factory->CreatePathGeometry(&path));ComPtr<ID2D1GeometrySink> sink;Check(path->Open(&sink));
            Check(run->fontFace->GetGlyphRunOutline(run->fontEmSize,run->glyphIndices,run->glyphAdvances,run->glyphOffsets,run->glyphCount,run->isSideways,run->bidiLevel%2,sink.Get()));Check(sink->Close());
            D2D1_MATRIX_3X2_F old;dc->GetTransform(&old);dc->SetTransform(D2D1::Matrix3x2F::Translation(x,y)*old);
            if(width>0)dc->DrawGeometry(path.Get(),edge,width);dc->FillGeometry(path.Get(),fill);dc->SetTransform(old);return S_OK;
        } catch(HRESULT hr){return hr;}
    }
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*,FLOAT x,FLOAT y,DWRITE_MEASURING_MODE mode,const DWRITE_GLYPH_RUN* run,const DWRITE_GLYPH_RUN_DESCRIPTION* description,IUnknown*) override {
        // Keep system shaping/fallback and advances unchanged. Only rasterization
        // differs; unsupported glyphs retain the existing outlined text rendering.
        if(!colorWrite||!colorDc)return Monochrome(x,y,run);
        constexpr auto formats=static_cast<DWRITE_GLYPH_IMAGE_FORMATS>(DWRITE_GLYPH_IMAGE_FORMATS_TRUETYPE|DWRITE_GLYPH_IMAGE_FORMATS_CFF|DWRITE_GLYPH_IMAGE_FORMATS_COLR|DWRITE_GLYPH_IMAGE_FORMATS_SVG|DWRITE_GLYPH_IMAGE_FORMATS_PNG|DWRITE_GLYPH_IMAGE_FORMATS_JPEG|DWRITE_GLYPH_IMAGE_FORMATS_TIFF|DWRITE_GLYPH_IMAGE_FORMATS_PREMULTIPLIED_B8G8R8A8);
        ComPtr<IDWriteColorGlyphRunEnumerator1> layers;
        HRESULT hr=colorWrite->TranslateColorGlyphRun(D2D1::Point2F(x,y),run,description,formats,mode,nullptr,0,&layers);
        if(hr==DWRITE_E_NOCOLOR)return Monochrome(x,y,run);
        if(FAILED(hr))return hr;
        try {
            ComPtr<ID2D1SolidColorBrush> palette;Check(dc->CreateSolidColorBrush(D2D1::ColorF(0,0),&palette));
            for(;;) {
                BOOL more=FALSE;Check(layers->MoveNext(&more));if(!more)break;
                const DWRITE_COLOR_GLYPH_RUN1* layer;Check(layers->GetCurrentRun(&layer));
                auto origin=D2D1::Point2F(layer->baselineOriginX,layer->baselineOriginY);
                if(shadowOnly) {
                    if(layer->paletteIndex==0xffff && (layer->glyphImageFormat==DWRITE_GLYPH_IMAGE_FORMATS_TRUETYPE || layer->glyphImageFormat==DWRITE_GLYPH_IMAGE_FORMATS_CFF || layer->glyphImageFormat==DWRITE_GLYPH_IMAGE_FORMATS_COLR))Check(Monochrome(origin.x,origin.y,&layer->glyphRun));
                    continue;
                }
                switch(layer->glyphImageFormat) {
                case DWRITE_GLYPH_IMAGE_FORMATS_PNG:case DWRITE_GLYPH_IMAGE_FORMATS_JPEG:
                case DWRITE_GLYPH_IMAGE_FORMATS_TIFF:case DWRITE_GLYPH_IMAGE_FORMATS_PREMULTIPLIED_B8G8R8A8:
                    colorDc->DrawColorBitmapGlyphRun(layer->glyphImageFormat,origin,&layer->glyphRun,mode);break;
                case DWRITE_GLYPH_IMAGE_FORMATS_SVG:
                    colorDc->DrawSvgGlyphRun(origin,&layer->glyphRun,fill,nullptr,0,mode);break;
                default:
                    if(layer->paletteIndex==0xffff)Check(Monochrome(origin.x,origin.y,&layer->glyphRun));
                    else {
                        palette->SetColor({layer->runColor.r,layer->runColor.g,layer->runColor.b,layer->runColor.a});
                        // Color layers must not receive the normal text outline:
                        // outlining each layer obscures the emoji's inner details.
                        dc->DrawGlyphRun(origin,&layer->glyphRun,palette.Get(),mode);
                    }
                    break;
                }
            }
            return S_OK;
        }catch(HRESULT error){return error;}
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
    ComPtr<IDWriteFactory4> colorWrite;ComPtr<ID2D1DeviceContext4> colorDc;bool colorEmoji=true;
    ComPtr<IDXGISwapChain1> swap;ComPtr<ID2D1Bitmap1> target;ComPtr<ID3D11Texture2D> offscreen;
    ComPtr<IDCompositionDevice> composition;ComPtr<IDCompositionTarget> compositionTarget;ComPtr<IDCompositionVisual> root;
    std::unordered_map<std::wstring,Glyph> cache;std::unordered_map<int,std::wstring> keys;size_t cacheBytes=0;
    DWRITE_FONT_WEIGHT fontWeight=DWRITE_FONT_WEIGHT_BOLD;float outlineWidth=2.5f,shadowOffset=0;
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
        // Optional interfaces: if unavailable, all text follows the mono path.
        write.As(&colorWrite);dc.As(&colorDc);
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
        Glyph result;ComPtr<IDWriteTextFormat> format;Check(write->CreateTextFormat(font.c_str(),nullptr,fontWeight,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,s.size,L"ja-JP",&format));
        Check(format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP));ComPtr<IDWriteTextLayout> layout;
        Check(write->CreateTextLayout(s.text.c_str(),static_cast<UINT32>(s.text.size()),format.Get(),100000,100000,&layout));
        DWRITE_TEXT_METRICS metrics{};DWRITE_OVERHANG_METRICS over{};Check(layout->GetMetrics(&metrics));Check(layout->GetOverhangMetrics(&over));
        float padding=std::ceil(outlineWidth/2)+2;float padX=padding+std::max(0.f,over.left),padY=padding+std::max(0.f,over.top);
        UINT w=static_cast<UINT>(std::ceil(metrics.widthIncludingTrailingWhitespace+padX+padding+shadowOffset+std::max(0.f,over.right)));
        UINT h=static_cast<UINT>(std::ceil(metrics.height+padY+padding+shadowOffset+std::max(0.f,over.bottom)));
        // Bound total texture storage for pathological multi-line comments.
        if(!w||!h||uint64_t(w)*h>16*1024*1024)throw E_INVALIDARG;
        ComPtr<ID2D1SolidColorBrush> fill,edge,shadow;
        Check(dc->CreateSolidColorBrush(D2D1::ColorF((s.color>>16&255)/255.f,(s.color>>8&255)/255.f,(s.color&255)/255.f),&fill));
        Check(dc->CreateSolidColorBrush(D2D1::ColorF((s.color&0xffffff)==0?D2D1::ColorF::White:D2D1::ColorF::Black),&edge));
        Check(dc->CreateSolidColorBrush(D2D1::ColorF(0.f,0.f,0.f,.65f),&shadow));
        Outline shadowRenderer(dc.Get(),factory.Get(),shadow.Get(),shadow.Get(),colorEmoji?colorWrite.Get():nullptr,colorEmoji?colorDc.Get():nullptr,outlineWidth,true);
        Outline renderer(dc.Get(),factory.Get(),fill.Get(),edge.Get(),colorEmoji?colorWrite.Get():nullptr,colorEmoji?colorDc.Get():nullptr,outlineWidth);
        auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED));
        for(UINT y=0;y<h;y+=2048)for(UINT x=0;x<w;x+=2048) {
            UINT tw=std::min(2048u,w-x),th=std::min(2048u,h-y);Tile tile;tile.x=x-padX;tile.y=y-padY;tile.w=float(tw);tile.h=float(th);
            Check(dc->CreateBitmap(D2D1::SizeU(tw,th),nullptr,0,&props,&tile.bitmap));dc->SetTarget(tile.bitmap.Get());dc->BeginDraw();dc->SetTransform(D2D1::Matrix3x2F::Identity());dc->Clear(D2D1::ColorF(0,0));
            HRESULT textResult=S_OK;
            // Render all shadows before any foreground glyphs, including fallback font runs.
            if(shadowOffset>0)textResult=layout->Draw(nullptr,&shadowRenderer,padX-x+shadowOffset,padY-y+shadowOffset);
            if(SUCCEEDED(textResult))textResult=layout->Draw(nullptr,&renderer,padX-x,padY-y);HRESULT endResult=dc->EndDraw();dc->SetTarget(target.Get());Check(textResult);Check(endResult);
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
    struct Signature {double center=0;uint64_t alpha=0,hash=14695981039346656037ull;int transparent=0,white=0,black=0,colored=0,right=-1,bottom=-1;};
    Signature Inspect() {
        D3D11_TEXTURE2D_DESC desc{};offscreen->GetDesc(&desc);desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> read;Check(d3d->CreateTexture2D(&desc,nullptr,&read));immediate->CopyResource(read.Get(),offscreen.Get());D3D11_MAPPED_SUBRESOURCE map{};Check(immediate->Map(read.Get(),0,D3D11_MAP_READ,0,&map));
        Signature s;double moment=0;
        for(UINT y=0;y<height;y++) {auto p=static_cast<BYTE*>(map.pData)+y*map.RowPitch;for(UINT x=0;x<width;x++,p+=4){s.alpha+=p[3];if(p[3]>10){s.right=std::max(s.right,int(x));s.bottom=std::max(s.bottom,int(y));}moment+=double(x)*p[3];for(int c=0;c<4;c++)s.hash=(s.hash^p[c])*1099511628211ull;if(!p[3])s.transparent++;if(p[3]>250){if(p[0]>250&&p[1]>250&&p[2]>250)s.white++;if(p[0]<5&&p[1]<5&&p[2]<5)s.black++;if(std::max({p[0],p[1],p[2]})-std::min({p[0],p[1],p[2]})>25)s.colored++;}}}
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
    if(message==WM_NCCREATE)SetWindowLongPtrW(hwnd,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams));
    if(message==WM_DISPLAYCHANGE) {
        auto timing=reinterpret_cast<DisplayTiming*>(GetWindowLongPtrW(hwnd,GWLP_USERDATA));
        if(timing)timing->Invalidate();
    }
    if(message==WM_NCDESTROY)SetWindowLongPtrW(hwnd,GWLP_USERDATA,0);
    if(message==WM_NCHITTEST)return HTTRANSPARENT;if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
    if(message==WM_ERASEBKGND)return 1;if(message==WM_PAINT){PAINTSTRUCT p;BeginPaint(hwnd,&p);EndPaint(hwnd,&p);return 0;}
    return DefWindowProcW(hwnd,message,w,l);
}
class Renderer {
public:
    inline static std::atomic<int> active=0;
    std::mutex gate;std::condition_variable initialized;std::atomic<bool> stop=false;bool ready=false;HRESULT initError=S_OK;
    HWND player=nullptr;bool enabled=false,moving=false;double stamp=0,lease=0;std::vector<Sprite> scene;Stats stats{};
    unsigned visibilityEpoch=0;bool hideRequested=false;
    DisplayStats displayStats{0,60,1};
    PacingStats pacingStats{};
    std::wstring font;float opacity,outlineWidth,shadowOffset;DWRITE_FONT_WEIGHT fontWeight;bool colorEmoji;std::thread thread;
    Renderer(const wchar_t* family,float alpha,DWRITE_FONT_WEIGHT weight,bool color,float stroke=2.5f,float shadow=0):font(family),opacity(alpha),outlineWidth(stroke),shadowOffset(shadow),fontWeight(weight),colorEmoji(color) {thread=std::thread([this]{Run();});std::unique_lock lock(gate);initialized.wait(lock,[this]{return ready;});active++;}
    ~Renderer(){stop=true;if(thread.joinable())thread.join();active--;}
    void Run() {
        ComScope com;HWND window=nullptr;DisplayTiming timing;
        try {
            Graphics g;g.font=font;g.opacity=opacity;g.fontWeight=fontWeight;g.colorEmoji=colorEmoji;g.outlineWidth=outlineWidth;g.shadowOffset=shadowOffset;g.Init();
            WNDCLASSW wc{};wc.lpfnWndProc=WindowProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"NicoBEGpuOverlay";RegisterClassW(&wc);
            window=CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP|WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,wc.lpszClassName,L"NicoBE GPU コメント",WS_POPUP,0,0,1,1,nullptr,nullptr,wc.hInstance,&timing);
            if(!window)throw HRESULT_FROM_WIN32(GetLastError());Check(SetLayeredWindowAttributes(window,0,255,LWA_ALPHA)?S_OK:HRESULT_FROM_WIN32(GetLastError()));g.Attach(window);
            {std::lock_guard lock(gate);stats.hardware=1;wcsncpy_s(stats.adapter,g.adapter.c_str(),_TRUNCATE);ready=true;initialized.notify_one();}
            Timer timer;double deadline=Now(),sampleStart=deadline,lastFrame=0;int frames=0,presents=0;unsigned busy=0;double cpu=0,cacheTime=0;HWND owner=nullptr;RECT previous{};
            ComPtr<IDXGIOutput> syncOutput;std::vector<double> frameIntervals;
            std::future<DisplaySource> displayRead;HMONITOR readingMonitor=nullptr;unsigned readingRevision=0;
            while(!stop) {
                MSG message;while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
                HWND host;bool show,animate,hide;unsigned epoch;double timestamp,alive;std::vector<Sprite> items;
                {std::lock_guard lock(gate);host=player;show=enabled;animate=moving;timestamp=stamp;alive=lease;items=scene;epoch=visibilityEpoch;hide=hideRequested;hideRequested=false;g.opacity=opacity;}
                if(hide&&IsWindowVisible(window))ShowWindow(window,SW_HIDE);
                double now=Now();RECT rect{};POINT point{};HWND video=host?GetDlgItem(host,59648):nullptr;
                show=show && now-timestamp<2 && host && IsWindow(host) && IsWindowVisible(host) && !IsIconic(host) && video && GetClientRect(video,&rect) && rect.right>20 && rect.bottom>20 && ClientToScreen(video,&point);
                int pending=0;
                if(show) {
                    rect.left=point.x;rect.top=point.y;rect.right+=point.x;rect.bottom+=point.y;
                    if(owner!=host){SetWindowLongPtrW(window,GWLP_HWNDPARENT,reinterpret_cast<LONG_PTR>(host));owner=host;}
                    bool first=!IsWindowVisible(window),moved=!EqualRect(&previous,&rect);
                    bool raise=GetForegroundWindow()==host && GetWindow(host,GW_HWNDPREV)!=window;
                    if(first||moved||raise) {
                        UINT flags=SWP_NOACTIVATE|SWP_NOOWNERZORDER;
                        // A tray click leaves focus outside the player. Restore the
                        // overlay just above its owner even without foreground focus,
                        // while leaving unrelated foreground windows above both.
                        HWND after=GetWindow(host,GW_HWNDPREV);
                        if((!first&&!raise)||after==window)flags|=SWP_NOZORDER;
                        SetWindowPos(window,after?after:HWND_TOP,rect.left,rect.top,rect.right-rect.left,rect.bottom-rect.top,flags);previous=rect;
                    }
                    g.Resize(rect.right-rect.left,rect.bottom-rect.top);
                    HMONITOR monitor=MonitorFromRect(&rect,MONITOR_DEFAULTTONEAREST);
                    // QueryDisplayConfig can enter the display driver. Keep that work
                    // off the animation thread and discard results made stale by a
                    // monitor move or another display-change notification.
                    if(displayRead.valid() && displayRead.wait_for(std::chrono::seconds(0))==std::future_status::ready) {
                        auto source=displayRead.get();RefreshRate rate=source.rate;
                        if(timing.CanApply(readingMonitor,readingRevision,monitor)) {
                            if(monitor!=timing.monitor || rate.numerator!=timing.rate.numerator || rate.denominator!=timing.rate.denominator){lastFrame=0;frameIntervals.clear();}
                            syncOutput=source.output;
                            timing.Updated(monitor,now,rate);
                            std::lock_guard lock(gate);displayStats={rate.Hz(),rate.Target(),rate.Divisor()};
                        }
                    }
                    if(!displayRead.valid() && timing.NeedsRefresh(true,first,monitor,now)) {
                        readingMonitor=monitor;readingRevision=timing.revision;
                        displayRead=std::async(std::launch::async,[monitor]{try{ComScope com;return ReadDisplaySource(monitor);}catch(...){return DisplaySource{};}});
                    }
                    // A composition swap chain's latency event can follow the primary
                    // display, even on a different output. Wait on this output instead.
                    if(monitor!=timing.monitor)syncOutput.Reset();
                    if(syncOutput) {
                        for(UINT tick=0;tick<timing.rate.Divisor()&&!stop;tick++) {
                            if(FAILED(syncOutput->WaitForVBlank())){syncOutput.Reset();break;}
                        }
                    }
                    if(stop)break;
                    {std::lock_guard lock(gate);if(!enabled||player!=host||epoch!=visibilityEpoch)continue;animate=moving;timestamp=stamp;alive=lease;items=scene;}
                    g.frame++;
                    double began=Now();pending=g.Prepare(items,3.0);cacheTime+=(Now()-began)*1000;
                    g.Draw(items,std::min(std::max(0.,Now()-timestamp),alive),animate);
                    // Do not add another wait tied to the primary display. A busy
                    // queue drops this submission; the next frame uses current time.
                    HRESULT presented;
                    {
                        std::lock_guard lock(gate);
                        // A file switch can invalidate a frame while its glyphs
                        // are being prepared. Never publish that old frame.
                        if(!enabled||player!=host||epoch!=visibilityEpoch)continue;
                        presented=g.swap->Present(0,DXGI_PRESENT_DO_NOT_WAIT);
                    }
                    if(presented==DXGI_ERROR_WAS_STILL_DRAWING)busy++;
                    else {
                        Check(presented);
                        // A hidden swap chain still contains its previous image.
                        // Replace it before making the overlay visible again.
                        {
                            std::lock_guard lock(gate);
                            if(!enabled||player!=host||epoch!=visibilityEpoch)continue;
                            if(!IsWindowVisible(window))ShowWindow(window,SW_SHOWNOACTIVATE);
                        }
                        double at=Now();if(lastFrame)frameIntervals.push_back((at-lastFrame)*1000);lastFrame=at;frames++;
                    }
                    cpu+=(Now()-began)*1000;presents++;
                } else {lastFrame=0;if(IsWindowVisible(window))ShowWindow(window,SW_HIDE);}
                now=Now();if(now-sampleStart>=1) {
                    double mean=0;for(double span:frameIntervals)mean+=span;
                    if(!frameIntervals.empty())mean/=frameIntervals.size();std::sort(frameIntervals.begin(),frameIntervals.end());
                    std::lock_guard lock(gate);stats.fps=frames/(now-sampleStart);stats.cpuMs=presents?cpu/presents:0;stats.cacheMs=presents?cacheTime/presents:0;stats.visible=show?static_cast<int>(items.size()):0;stats.pending=pending;
                    pacingStats={mean,frameIntervals.empty()?0:frameIntervals[frameIntervals.size()*95/100],frameIntervals.empty()?0:frameIntervals.back(),busy,show&&syncOutput?1:0};
                    frames=presents=0;busy=0;cpu=cacheTime=0;sampleStart=now;frameIntervals.clear();
                }
                if(!show||!syncOutput){deadline+=1./(show?timing.rate.Target():60.);if(deadline<now)deadline=now;timer.Until(deadline);}
                else deadline=now;
            }
        } catch(HRESULT hr) {std::lock_guard lock(gate);stats.error=static_cast<unsigned>(hr);initError=hr;ready=true;initialized.notify_one();}
          catch(...) {std::lock_guard lock(gate);stats.error=static_cast<unsigned>(E_FAIL);initError=E_FAIL;ready=true;initialized.notify_one();}
        if(window)DestroyWindow(window);
    }
};
#define API extern "C" __declspec(dllexport)
API HRESULT __cdecl NicoCreateStyledWithShadow(const wchar_t* font,int weight,int colorEmoji,float opacity,float outlineWidth,float shadowOffset,Renderer** output) {
    if(!output)return E_INVALIDARG;*output=nullptr;
    if(!font||!std::isfinite(outlineWidth)||outlineWidth<0||outlineWidth>8||!std::isfinite(shadowOffset)||shadowOffset<0||shadowOffset>8||(weight!=400 && weight!=700 && weight!=900))return E_INVALIDARG;
    try {auto r=new Renderer(font,opacity,static_cast<DWRITE_FONT_WEIGHT>(weight),colorEmoji!=0,outlineWidth,shadowOffset);if(FAILED(r->initError)){HRESULT hr=r->initError;delete r;return hr;}*output=r;return S_OK;}catch(...){return E_FAIL;}
}
API HRESULT __cdecl NicoCreateStyled(const wchar_t* font,int weight,int colorEmoji,float opacity,float outlineWidth,Renderer** output) {return NicoCreateStyledWithShadow(font,weight,colorEmoji,opacity,outlineWidth,0,output);}
API HRESULT __cdecl NicoCreateWithOptions(const wchar_t* font,int weight,int colorEmoji,float opacity,Renderer** output) {return NicoCreateStyled(font,weight,colorEmoji,opacity,2.5f,output);}
API HRESULT __cdecl NicoCreateWithWeight(const wchar_t* font,int weight,float opacity,Renderer** output) {return NicoCreateWithOptions(font,weight,0,opacity,output);}
API HRESULT __cdecl NicoCreate(const wchar_t* font,float opacity,Renderer** output) {return NicoCreateWithWeight(font,700,opacity,output);}
API HRESULT __cdecl NicoSetOpacity(Renderer* r,float opacity) {
    if(!r||!std::isfinite(opacity)||opacity<.1f||opacity>1.f)return E_INVALIDARG;
    std::lock_guard lock(r->gate);r->opacity=opacity;return S_OK;
}
API HRESULT __cdecl NicoValidateOpacity(int* checks) {
    if(!checks)return E_POINTER;*checks=0;
    try {
        ComScope com;Graphics g;g.Init();g.Resize(640,180);
        std::vector<Sprite> items{{1,100,50,32,0xffffff,0,5,L"不透明度 ABC"}};
        g.frame++;g.Prepare(items,10000);
        g.opacity=1;g.Draw(items,0,true);auto full=g.Inspect();if(!full.alpha)throw E_FAIL;(*checks)++;
        g.opacity=.5f;g.Draw(items,0,true);auto half=g.Inspect();double ratio=double(half.alpha)/full.alpha;
        if(ratio<.47||ratio>.53)throw E_FAIL;(*checks)++;
        if(std::abs(half.center-full.center)>.5)throw E_FAIL;(*checks)++;
        g.opacity=.1f;g.Draw(items,0,true);auto low=g.Inspect();ratio=double(low.alpha)/full.alpha;
        if(ratio<.08||ratio>.12)throw E_FAIL;(*checks)++;
        g.opacity=1;g.Draw(items,0,true);if(g.Inspect().hash!=full.hash)throw E_FAIL;(*checks)++;
        std::unique_ptr<Renderer> r(new Renderer(L"Yu Gothic UI",.95f,DWRITE_FONT_WEIGHT_BOLD,false));
        Check(r->initError);Check(NicoSetOpacity(r.get(),.6f));
        {std::lock_guard lock(r->gate);if(r->opacity!=.6f)throw E_FAIL;}(*checks)++;
        if(SUCCEEDED(NicoSetOpacity(r.get(),0))||SUCCEEDED(NicoSetOpacity(nullptr,.5f)))throw E_FAIL;(*checks)++;
        return S_OK;
    } catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}
}
API void __cdecl NicoDestroy(Renderer* r){delete r;}
API int __cdecl NicoActiveRenderers(){return Renderer::active.load();}
API void __cdecl NicoFollow(Renderer* r,HWND player,int enabled){if(!r)return;std::lock_guard lock(r->gate);if(r->player!=player||(r->enabled&&!enabled)){r->visibilityEpoch++;r->hideRequested=true;}r->player=player;r->enabled=enabled!=0;}
API void __cdecl NicoSubmit(Renderer* r,const Item* items,int count,int moving,double lease) {
    if(!r||count<0||count>300||(!items&&count))return;
    std::vector<Sprite> scene;scene.reserve(count);for(int i=0;i<count;i++){auto& s=items[i];scene.push_back({s.id,s.x,s.y,s.size,s.color,s.velocity,s.remaining,s.text?s.text:L""});}
    std::lock_guard lock(r->gate);r->scene=std::move(scene);r->stamp=Now();r->moving=moving!=0;r->lease=std::clamp(lease,0.,.65);
}
API void __cdecl NicoStats(Renderer* r,Stats* stats){if(!r||!stats)return;std::lock_guard lock(r->gate);*stats=r->stats;}
API void __cdecl NicoDisplayStats(Renderer* r,DisplayStats* stats){if(!r||!stats)return;std::lock_guard lock(r->gate);*stats=r->displayStats;}
API void __cdecl NicoPacingStats(Renderer* r,PacingStats* stats){if(!r||!stats)return;std::lock_guard lock(r->gate);*stats=r->pacingStats;}
API HRESULT __cdecl NicoValidateFrameSync(int* checks) {
    if(!checks)return E_POINTER;*checks=0;ComScope com;HWND window=nullptr;DisplayTiming timing;
    try {
        auto verify=[&](bool ok){if(!ok)throw E_FAIL;(*checks)++;};
        struct Case {UINT n,d,divisor;double target;};
        const Case cases[]={{0,0,1,60},{50,1,1,50},{59,1,1,59},{60000,1001,1,60000./1001},
            {60,1,1,60},{75,1,1,75},{119,1,1,119},{120000,1001,1,120000./1001},
            {120,1,2,60},{144,1,2,72},{165,1,2,82.5},{180,1,3,60},{240,1,4,60},{300,1,5,60},{360,1,6,60},{480,1,8,60}};
        for(const auto& c:cases){RefreshRate rate{c.n,c.d};verify(rate.Divisor()==c.divisor && std::abs(rate.Target()-c.target)<.00001);}
        RefreshRate actual=ReadRefreshRate(MonitorFromPoint(POINT{0,0},MONITOR_DEFAULTTOPRIMARY));
        verify(actual.Known()&&actual.Target()>0&&actual.Divisor()>=1);
        auto source=ReadDisplaySource(MonitorFromPoint(POINT{0,0},MONITOR_DEFAULTTOPRIMARY));
        verify(source.output&&SUCCEEDED(source.output->WaitForVBlank()));
        auto a=reinterpret_cast<HMONITOR>(1),b=reinterpret_cast<HMONITOR>(2);
        verify(!timing.NeedsRefresh(false,false,a,0));
        verify(timing.NeedsRefresh(true,true,a,0));timing.Updated(a,0,{75,1});
        verify(!timing.NeedsRefresh(true,false,a,4.999)&&timing.NeedsRefresh(true,false,a,5));
        verify(timing.NeedsRefresh(true,false,b,1));
        DisplayTiming other;other.Updated(b,0,{144,1});verify(timing.rate.Target()==75&&other.rate.Target()==72);
        timing.Invalidate();verify(timing.NeedsRefresh(true,false,a,1)&&!timing.NeedsRefresh(false,false,a,6));
        timing.Updated(a,1,{165,1});verify(timing.rate.Target()==82.5&&other.rate.Target()==72);
        verify(timing.CanApply(a,timing.revision,a)&&!timing.CanApply(a,timing.revision,b));
        unsigned oldRevision=timing.revision;timing.Invalidate();verify(!timing.CanApply(a,oldRevision,a));
        timing.Updated(a,1,{165,1});
        WNDCLASSW wc{};wc.lpfnWndProc=WindowProc;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"NicoBEFrameSyncTest";RegisterClassW(&wc);
        window=CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,wc.lpszClassName,L"NicoBE frame sync test",WS_POPUP,-32000,-32000,128,72,nullptr,nullptr,wc.hInstance,&timing);
        if(!window)throw HRESULT_FROM_WIN32(GetLastError());
        SendMessageW(window,WM_DISPLAYCHANGE,32,MAKELPARAM(1920,1080));
        verify(timing.dirty&&!other.dirty&&!timing.NeedsRefresh(false,false,a,10));
        for(int pass=0;pass<2;pass++) {
            Graphics g;g.Init();g.Attach(window);
            ComPtr<IDXGIDevice1> device;Check(g.dxgi.As(&device));UINT latency=0;Check(device->GetMaximumFrameLatency(&latency));
            if(latency!=1)throw E_FAIL;(*checks)++;
            for(auto size:{128U,256U}) {
                g.Resize(size,size);DXGI_SWAP_CHAIN_DESC1 desc{};Check(g.swap->GetDesc1(&desc));
                if(desc.Flags!=0||desc.Width!=size)throw E_FAIL;(*checks)++;
            }
            g.Draw({},0,false);Check(g.swap->Present(0,DXGI_PRESENT_DO_NOT_WAIT));(*checks)++;
        }
        DestroyWindow(window);return S_OK;
    }catch(HRESULT hr){if(window)DestroyWindow(window);return hr;}catch(...){if(window)DestroyWindow(window);return E_FAIL;}
}
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
// Short offscreen regression test; never creates or displays a desktop window.
API HRESULT __cdecl NicoValidateOutline(const wchar_t* png,int* checks) {
    if(!checks)return E_POINTER;*checks=0;ComScope com;
    try {
        Graphics g;g.Init();g.Resize(1920,1080);g.opacity=1;
        unsigned long long previous=0;
        for(float width:{0.f,1.5f,2.5f,4.f,6.f,8.f}) {
            g.outlineWidth=width;g.cache.clear();g.keys.clear();g.cacheBytes=0;
            std::vector<Sprite> items{{1,20,20,48,0xffffff,0,-1,L"縁取り 日本語 ABC 123"}};
            g.frame++;g.Prepare(items,10000);g.Draw(items,0,false);auto pixels=g.Inspect();
            if(pixels.alpha<=previous||pixels.white==0)throw E_FAIL;previous=pixels.alpha;(*checks)++;
            if(width==4.f&&png)g.SavePng(png);
        }
        return S_OK;
    }catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}
}
API HRESULT __cdecl NicoValidateShadow(const wchar_t* png,int* checks) {
    if(!checks)return E_POINTER;*checks=0;ComScope com;
    try {
        Graphics g;g.Init();g.Resize(1920,1080);g.opacity=1;g.outlineWidth=2.5f;
        Graphics::Signature baseline;
        for(float offset:{0.f,2.f,3.f,4.f,6.f,8.f}) {
            g.shadowOffset=offset;g.cache.clear();g.keys.clear();g.cacheBytes=0;
            std::vector<Sprite> items{{1,60,60,48,0xffffff,0,-1,L"右下の影 日本語 ABC\n複数行の描画"}};
            g.frame++;g.Prepare(items,10000);g.Draw(items,0,false);auto pixels=g.Inspect();
            if(offset==0)baseline=pixels;
            if(!pixels.white||std::abs(pixels.right-baseline.right-offset)>1||std::abs(pixels.bottom-baseline.bottom-offset)>1)throw E_FAIL;
            if(offset>0 && pixels.alpha<=baseline.alpha)throw E_FAIL;
            (*checks)++;
            if(offset==4.f&&png){g.Draw(items,0,false,true);g.SavePng(png);}
        }
        // Outline-off keeps the same glyph fill and still supports a shadow.
        g.outlineWidth=0;g.shadowOffset=4;g.cache.clear();g.keys.clear();g.cacheBytes=0;
        std::vector<Sprite> plain{{2,60,60,48,0xffffff,0,-1,L"縁取りなし・影あり"}};
        g.frame++;g.Prepare(plain,10000);g.Draw(plain,0,false);if(!g.Inspect().white)throw E_FAIL;(*checks)++;
        return S_OK;
    }catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}
}
API HRESULT __cdecl NicoValidateColorEmoji(const wchar_t* colorPng,const wchar_t* monoPng,int* checks) {
    if(!checks)return E_POINTER;*checks=0;ComScope com;
    try {
        Graphics g;g.Init();g.Resize(1920,1080);g.opacity=1;
        auto render=[&](bool color,const wchar_t* text) {
            g.colorEmoji=color;g.cache.clear();g.keys.clear();g.cacheBytes=0;
            std::vector<Sprite> items{{1,60,60,64,0xffffff,0,-1,text}};
            g.frame++;g.Prepare(items,10000);g.Draw(items,0,false);return g.Inspect();
        };
        const wchar_t* sample=L"日本語 ABC 😀 🎉 👍🏽 ❤️ 1️⃣\n家族 👨‍👩‍👧‍👦 絵文字と通常の文字\n単色の記号 ∑ ∫ → 未対応文字 \U0010ffff";
        auto colored=render(true,sample);if(colored.colored<100||colored.white==0||colored.black==0)throw E_FAIL;(*checks)++;
        if(colorPng)g.SavePng(colorPng);
        auto mono=render(false,sample);if(mono.colored!=0||mono.alpha==0)throw E_FAIL;(*checks)++;
        if(monoPng)g.SavePng(monoPng);
        const wchar_t* fallback=L"日本語 ABC ∑ ∫ → \U0010ffff";
        auto fallbackOn=render(true,fallback),fallbackOff=render(false,fallback);
        if(fallbackOn.hash!=fallbackOff.hash||fallbackOn.alpha==0)throw E_FAIL;(*checks)++;
        g.colorWrite.Reset();auto unavailable=render(true,sample);
        if(unavailable.hash!=mono.hash)throw E_FAIL;(*checks)++;
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
