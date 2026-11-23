#define NOMINMAX
#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <audiopolicy.h>
#include <mmdeviceapi.h>
#include <tlhelp32.h>
#include <wrl.h>
#include <wrl/implements.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <thread>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace {
void check(HRESULT result) { if (FAILED(result)) throw result; }
struct Handle {
    HANDLE value;
    ~Handle(){ if(value && value!=INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct Activation final : Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IActivateAudioInterfaceCompletionHandler, Microsoft::WRL::FtmBase> {
    Handle done{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    HRESULT result=E_FAIL;
    ComPtr<IAudioClient> client;
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* op) override {
        ComPtr<IUnknown> object;
        HRESULT hr=op->GetActivateResult(&result,&object);
        if(FAILED(hr))result=hr;
        if(SUCCEEDED(result)) result=object.As(&client);
        SetEvent(done.value); return S_OK;
    }
};
ComPtr<IAudioClient> capture_client(DWORD pid) {
    AUDIOCLIENT_ACTIVATION_PARAMS params{};
    params.ActivationType=AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    params.ProcessLoopbackParams.TargetProcessId=pid;
    params.ProcessLoopbackParams.ProcessLoopbackMode=PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
    PROPVARIANT value{}; value.vt=VT_BLOB;value.blob.cbSize=sizeof(params);value.blob.pBlobData=reinterpret_cast<BYTE*>(&params);
    auto cb=Microsoft::WRL::Make<Activation>(); ComPtr<IActivateAudioInterfaceAsyncOperation> op;
    // Load the audio API on demand. Loading MMDevAPI ahead of the GUI can
    // preselect legacy Common Controls in console hosts and in unit tests.
    static auto activate=[](){
        HMODULE mm=LoadLibraryExW(L"Mmdevapi.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
        return mm?reinterpret_cast<decltype(&ActivateAudioInterfaceAsync)>(GetProcAddress(mm,"ActivateAudioInterfaceAsync")):nullptr;
    }();
    if(!activate)throw E_NOTIMPL;
    check(activate(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,__uuidof(IAudioClient),&value,cb.Get(),&op));
    if(WaitForSingleObject(cb->done.value,3500)!=WAIT_OBJECT_0)throw E_ABORT;
    check(cb->result);return cb->client;
}
std::set<DWORD> descendants(DWORD root) {
    std::set<DWORD> nodes{root}; std::map<DWORD,DWORD> parents;
    Handle snap{CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0)};
    PROCESSENTRY32W p{};p.dwSize=sizeof(p);
    if(Process32FirstW(snap.value,&p)){do {parents[p.th32ProcessID]=p.th32ParentProcessID;}while(Process32NextW(snap.value,&p));}
    size_t count;
    do {count=nodes.size();for(auto [pid,parent]:parents)if(nodes.count(parent))nodes.insert(pid);}while(count!=nodes.size());
    return nodes;
}
struct Source { DWORD pid;ComPtr<IMMDevice> device;ComPtr<ISimpleAudioVolume> volume;ComPtr<IAudioSessionControl2> control; };
bool find_source(IMMDeviceEnumerator* devices,DWORD root,Source& source) {
    const auto pids=descendants(root);ComPtr<IMMDeviceCollection> all;check(devices->EnumAudioEndpoints(eRender,DEVICE_STATE_ACTIVE,&all));
    UINT n=0;check(all->GetCount(&n));
    for(UINT i=0;i<n;i++) {
        ComPtr<IMMDevice> device;ComPtr<IAudioSessionManager2> manager;ComPtr<IAudioSessionEnumerator> sessions;
        if(FAILED(all->Item(i,&device))||FAILED(device->Activate(__uuidof(IAudioSessionManager2),CLSCTX_ALL,nullptr,&manager))||FAILED(manager->GetSessionEnumerator(&sessions))) continue;
        int m=0;check(sessions->GetCount(&m));
        for(int j=0;j<m;j++){
            ComPtr<IAudioSessionControl> base;ComPtr<IAudioSessionControl2> control;ComPtr<ISimpleAudioVolume> volume;
            DWORD pid=0;AudioSessionState state;BOOL muted=TRUE;
            if(FAILED(sessions->GetSession(j,&base))||FAILED(base.As(&control))||control->GetProcessId(&pid)!=S_OK||!pids.count(pid)||pid==GetCurrentProcessId())continue;
            if(FAILED(control->GetState(&state))||state!=AudioSessionStateActive||FAILED(base.As(&volume))||FAILED(volume->GetMute(&muted))||muted)continue;
            source={pid,device,volume,control};return true;
        }
    }return false;
}
struct Restore {ISimpleAudioVolume* volume;bool muted=false;~Restore(){if(muted)volume->SetMute(FALSE,nullptr);}};
bool relay(Source& source,HANDLE stop) {
    auto input=capture_client(source.pid);ComPtr<IAudioClient> output;
    check(source.device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&output));
    WAVEFORMATEX format{WAVE_FORMAT_PCM,2,48000,192000,4,16,0};
    Handle available{CreateEventW(nullptr,FALSE,FALSE,nullptr)};
    if(!available.value)throw E_FAIL;
    check(input->Initialize(AUDCLNT_SHAREMODE_SHARED,AUDCLNT_STREAMFLAGS_LOOPBACK|AUDCLNT_STREAMFLAGS_EVENTCALLBACK,200000,0,&format,nullptr));
    check(input->SetEventHandle(available.value));
    check(output->Initialize(AUDCLNT_SHAREMODE_SHARED,AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM|AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,500000,0,&format,nullptr));
    ComPtr<IAudioCaptureClient> capture;ComPtr<IAudioRenderClient> render;ComPtr<ISimpleAudioVolume> host;
    check(input->GetService(IID_PPV_ARGS(&capture)));check(output->GetService(IID_PPV_ARGS(&render)));check(output->GetService(IID_PPV_ARGS(&host)));
    UINT32 capacity=0;check(output->GetBufferSize(&capacity));check(input->Start());check(output->Start());
    Restore restore{source.volume.Get()};auto signalAt=GetTickCount64(),writtenAt=signalAt;
    HANDLE events[]{stop,available.value};
    while(WaitForMultipleObjects(2,events,FALSE,40)!=WAIT_OBJECT_0){
        AudioSessionState state;check(source.control->GetState(&state));if(state!=AudioSessionStateActive)return true;
        BOOL muted=TRUE;float gain=1,hostGain=1;check(host->GetMute(&muted));check(host->GetMasterVolume(&hostGain));
        if(muted||hostGain<=0)return false;
        check(source.volume->GetMasterVolume(&gain));
        for(;;){
            BYTE* data=nullptr;UINT32 frames=0;DWORD flags=0;
            HRESULT packet=capture->GetBuffer(&data,&frames,&flags,nullptr,nullptr);
            if(packet==AUDCLNT_S_BUFFER_EMPTY)break;
            check(packet);
            struct Release {IAudioCaptureClient* c;UINT32 n;~Release(){c->ReleaseBuffer(n);}} guard{capture.Get(),frames};
            UINT32 padding=0;check(output->GetCurrentPadding(&padding));UINT32 n=std::min(frames,capacity-std::min(padding,capacity));
            if(n){
                BYTE* dest=nullptr;check(render->GetBuffer(n,&dest));auto samples=reinterpret_cast<short*>(dest);bool signal=false;
                for(UINT32 i=0;i<2*n;i++){int sample=(flags&AUDCLNT_BUFFERFLAGS_SILENT)||!data?0:reinterpret_cast<const short*>(data)[i];
                    samples[i]=static_cast<short>(sample*gain);signal|=std::abs(samples[i])>3;}
                check(render->ReleaseBuffer(n,0));writtenAt=GetTickCount64();
                if(signal){signalAt=writtenAt;if(!restore.muted){check(source.volume->SetMute(TRUE,nullptr));restore.muted=true;}}
            }
        }
        auto now=GetTickCount64();if(now-signalAt>=1500 || now-writtenAt>=1500)return false;
    }return false;
}
struct Worker{Handle stop{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::thread thread;};
std::mutex mutex;std::map<DWORD,std::unique_ptr<Worker>> workers;
void run(DWORD pid,HANDLE stop) {
    if(FAILED(CoInitializeEx(nullptr,COINIT_MULTITHREADED)))return;
    try {
        Handle browser{OpenProcess(SYNCHRONIZE,FALSE,pid)};if(!browser.value)throw E_FAIL;
        ComPtr<IMMDeviceEnumerator> devices;check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&devices)));
        while(WaitForSingleObject(stop,400)==WAIT_TIMEOUT && WaitForSingleObject(browser.value,0)==WAIT_TIMEOUT){
            try {Source source{};if(find_source(devices.Get(),pid,source)&&!relay(source,stop)){
                AudioSessionState state;
                while(WaitForSingleObject(stop,500)==WAIT_TIMEOUT && SUCCEEDED(source.control->GetState(&state)) && state==AudioSessionStateActive){}
            }}catch(HRESULT){if(WaitForSingleObject(stop,1500)!=WAIT_TIMEOUT)break;}
        }
    }catch(...){}CoUninitialize();
}
}
extern "C" void umd_audio_attach(DWORD pid) noexcept {
    if(!pid || pid==GetCurrentProcessId())return;
    try {std::lock_guard<std::mutex> guard(mutex);if(workers.count(pid))return;
        auto w=std::make_unique<Worker>();auto entry=workers.emplace(pid,std::move(w)).first;
        entry->second->thread=std::thread(run,pid,entry->second->stop.value);
    }catch(...){}
}
extern "C" void umd_audio_shutdown() noexcept {
    std::lock_guard<std::mutex> guard(mutex);
    for(auto& entry:workers)SetEvent(entry.second->stop.value);
    for(auto& entry:workers)if(entry.second->thread.joinable())entry.second->thread.join();
    workers.clear();
}
