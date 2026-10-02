// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "learning_dispatch.hpp"
#include "module.hpp"
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace mansur::win {
HINSTANCE module_instance=nullptr;
std::atomic<long> live_objects{0};
std::atomic<long> server_locks{0};
unsigned LearningTestConnectAttempts() noexcept;
void LearningTestPauseBeforeWrite(HANDLE reached,HANDLE proceed) noexcept;
void LearningTestPartialWrite(bool partial) noexcept;
void LearningTestSetPipeName(std::wstring name);
}
using namespace mansur::win;
void check(bool value,const char* label) {if(!value)throw std::runtime_error(label);}
std::int64_t number(const std::string& packet,const std::string& name) {
    const auto key="\""+name+"\":";const auto at=packet.find(key);
    check(at!=std::string::npos,"required ordering field present");
    const auto value=std::stoll(packet.substr(at+key.size()));
    check(value>0,"ordering field positive Int64");return value;
}
std::string string_field(const std::string& packet,const std::string& name) {
    const auto key="\""+name+"\":\"";const auto at=packet.find(key);
    check(at!=std::string::npos,"required sender present");
    const auto start=at+key.size(),end=packet.find('"',start);
    check(end!=std::string::npos&&end>start,"sender not empty");return packet.substr(start,end-start);
}
struct Server {
    HANDLE pipe=INVALID_HANDLE_VALUE,event=nullptr;
    OVERLAPPED operation{};
    bool pending=false;
    Server(DWORD delay_before_connect=0,bool connect=true,DWORD access=PIPE_ACCESS_DUPLEX) {
        pipe=CreateNamedPipeW(LearningTestPipeName().c_str(),access|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,65536,65536,0,nullptr);
        check(pipe!=INVALID_HANDLE_VALUE,"private test pipe created");
        event=CreateEventW(nullptr,TRUE,FALSE,nullptr);check(event!=nullptr,"test event");
        operation.hEvent=event;
        if(!connect)return;
        if(delay_before_connect)Sleep(delay_before_connect);
        const BOOL connected=ConnectNamedPipe(pipe,&operation);
        const auto error=connected?ERROR_SUCCESS:GetLastError();
        check(connected||error==ERROR_IO_PENDING||error==ERROR_PIPE_CONNECTED,"test pipe listening");
        pending=!connected&&error==ERROR_IO_PENDING;
        if(!pending)SetEvent(event);
    }
    ~Server() {
        if(pipe!=INVALID_HANDLE_VALUE) {
            // An immediate broken-pipe ReadFile failure is not a queued I/O,
            // even if Windows left OVERLAPPED.Internal marked pending.
            if(pending){CancelIoEx(pipe,&operation);DWORD ignored=0;GetOverlappedResult(pipe,&operation,&ignored,TRUE);}
            DisconnectNamedPipe(pipe);CloseHandle(pipe);
        }
        if(event)CloseHandle(event);
    }
    void acknowledge(unsigned char ack=0x06) {
        ResetEvent(event);operation={};operation.hEvent=event;DWORD written=0;
        const BOOL done=WriteFile(pipe,&ack,1,&written,&operation);
        if(!done) {
            check(GetLastError()==ERROR_IO_PENDING,"ack write queued");pending=true;
            const auto wait=WaitForSingleObject(event,500);
            if(wait!=WAIT_OBJECT_0){CancelIoEx(pipe,&operation);GetOverlappedResult(pipe,&operation,&written,TRUE);pending=false;throw std::runtime_error("ack write timed out");}
            const auto completed=GetOverlappedResult(pipe,&operation,&written,FALSE);pending=false;
            check(completed!=FALSE,"ack write completed");
        }
        check(written==1,"ack is one fixed byte");
    }
    std::string read(bool allow_empty=false,bool send_ack=true) {
        check(WaitForSingleObject(event,2000)==WAIT_OBJECT_0,"client connected promptly");
        if(pending){DWORD ignored=0;GetOverlappedResult(pipe,&operation,&ignored,FALSE);pending=false;}
        ResetEvent(event);operation={};operation.hEvent=event;
        std::vector<char> buffer(65536);DWORD count=0;
        const BOOL done=ReadFile(pipe,buffer.data(),static_cast<DWORD>(buffer.size()),&count,&operation);
        if(!done) {
            const auto error=GetLastError();
            if(allow_empty&&error==ERROR_BROKEN_PIPE)return {};
            check(error==ERROR_IO_PENDING,"read queued");
            pending=true;
            const DWORD wait=WaitForSingleObject(event,2000);
            if(wait!=WAIT_OBJECT_0) {CancelIoEx(pipe,&operation);GetOverlappedResult(pipe,&operation,&count,TRUE);pending=false;throw std::runtime_error("test read timed out");}
            const auto completed=GetOverlappedResult(pipe,&operation,&count,FALSE);pending=false;
            if(!completed) {
                if(allow_empty&&GetLastError()==ERROR_BROKEN_PIPE)return {};
                check(false,"read completed");
            }
        }
        std::string packet{buffer.data(),count};
        if(send_ack&&!packet.empty()&&packet.back()=='\n')acknowledge();
        return packet;
    }
};
struct Client {
    HANDLE handle=INVALID_HANDLE_VALUE;
    Client() {
        handle=CreateFileW(LearningTestPipeName().c_str(),GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);
        check(handle!=INVALID_HANDLE_VALUE,"private test listener occupied");
    }
    ~Client(){if(handle!=INVALID_HANDLE_VALUE)CloseHandle(handle);}
};
struct PauseBeforeWrite {
    HANDLE reached=CreateEventW(nullptr,TRUE,FALSE,nullptr),proceed=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    PauseBeforeWrite(){check(reached&&proceed,"pause events created");LearningTestPauseBeforeWrite(reached,proceed);}
    ~PauseBeforeWrite(){LearningTestPauseBeforeWrite(nullptr,nullptr);SetEvent(proceed);CloseHandle(reached);CloseHandle(proceed);}
};
void wait_attempts(unsigned minimum) {
    const auto deadline=GetTickCount64()+1000;
    while(LearningTestConnectAttempts()<minimum&&GetTickCount64()<deadline)Sleep(2);
    check(LearningTestConnectAttempts()>=minimum,"background connection retry observed");
}
DeliverySnapshot wait_status(std::uint64_t context) {
    const auto deadline=GetTickCount64()+2500;
    while(GetTickCount64()<deadline) {
        const auto status=GetLastDeliveryStatus();
        if(status.context_id==context&&status.status!=DeliveryStatus::Queued&&status.status!=DeliveryStatus::Idle)return status;
        Sleep(5);
    }
    throw std::runtime_error("delivery status timed out");
}
void drain() {
    const auto deadline=GetTickCount64()+2500;
    while(live_objects.load()!=0&&GetTickCount64()<deadline)Sleep(5);
    check(live_objects.load()==0,"worker lifetime released");
}
int main(int argc,char** argv) {
    try {
        mansur::Commit commit{1,L"你好，\"test\"。\n",true};
        if(argc==3&&std::string(argv[1])=="--managed-client") {
            std::wstring private_name;for(const unsigned char ch:std::string(argv[2]))private_name+=ch;
            check(private_name.find(L"MansurNext.Learning.ManagedTest.")!=std::wstring::npos,"managed test uses isolated name");
            LearningTestSetPipeName(L"\\\\.\\pipe\\"+private_name);
            check(LearningDispatch({commit,900,1,{}})==DispatchResult::Queued,"managed listener request queued");
            check(wait_status(900).status==DeliveryStatus::Delivered,"production managed listener acknowledges native packet");
            drain();std::cout<<"Native-to-managed private transport passed.\n";return 0;
        }
        {
            std::cerr<<"transport: fixed old close-before-connect race\n";
            Server server(0,false);
            {
                Client old_client;DWORD written=0;const char line[]="{}\n";
                check(WriteFile(old_client.handle,line,3,&written,nullptr)&&written==3,"legacy client writes before server waits");
            }
            const BOOL connected=ConnectNamedPipe(server.pipe,&server.operation);
            const auto error=connected?ERROR_SUCCESS:GetLastError();
            std::cerr<<"transport: old race ConnectNamedPipe error="<<error<<'\n';
            check(!connected&&error==ERROR_NO_DATA,"closed client before ConnectNamedPipe deterministically loses handshake");
        }
        std::cerr<<"transport: missing broker\n";
        const auto dispatch_begin=GetTickCount64();
        check(LearningDispatch({commit,100,1,{}})==DispatchResult::Queued,"missing broker queues without waiting");
        check(GetTickCount64()-dispatch_begin<400,"input caller does not wait for connection deadline");
        check(wait_status(100).status==DeliveryStatus::Unavailable,"missing broker reported honestly");drain();
        std::string first_context,first_sender;std::int64_t first_ticks=0,first_sequence=0;
        {
            std::cerr<<"transport: ordinary learn\n";
            Server server;
            LARGE_INTEGER before{},after{};check(QueryPerformanceCounter(&before)!=FALSE,"QPC available");
            check(LearningDispatch({commit,101,2,{{10,20,11,40},true}})==DispatchResult::Queued,"fresh attempt after broker starts");
            const auto packet=server.read();
            check(QueryPerformanceCounter(&after)!=FALSE,"QPC remains available");
            first_ticks=number(packet,"sent_ticks");first_sequence=number(packet,"sent_sequence");first_sender=string_field(packet,"sender");
            check(first_ticks>=before.QuadPart&&first_ticks<=after.QuadPart,"learn uses actual shared QPC clock");
            check(packet.find("\"op\":\"learn\"")!=std::string::npos,"learn operation");
            check(packet.find("\\\"test\\\"")!=std::string::npos&&packet.find("\\u000a")!=std::string::npos,"JSON escapes punctuation and newline");
            check(packet.find("\"revision\":2")!=std::string::npos,"source revision");
            check(packet.find("\"anchor\":{\"left\":10,\"top\":20,\"right\":11,\"bottom\":40}")!=std::string::npos,"caret coordinates");
            check(!packet.empty()&&packet.back()=='\n',"single line framing");
            const auto start=packet.find("\"context\":");const auto end=packet.find(',',start);
            first_context=packet.substr(start,end-start);
            check(wait_status(101).status==DeliveryStatus::Delivered,"pipe delivery confirmed");drain();
        }
        {
            std::cerr<<"transport: ordinary cancel\n";
            Server server;
            check(CancelLearning(101)==DispatchResult::Queued,"cancel queued");
            const auto packet=server.read();
            check(packet.find("\"op\":\"cancel\"")!=std::string::npos,"cancel operation");
            check(packet.find(first_context)!=std::string::npos,"cancel context identical to learn");
            check(packet.find("\"text\"")==std::string::npos,"cancel contains no input text");
            check(number(packet,"sent_ticks")>=first_ticks&&number(packet,"sent_sequence")>first_sequence,"cancel retains later send order");
            check(string_field(packet,"sender")==first_sender,"cancel sender identical to learn");
            check(wait_status(101).status==DeliveryStatus::Delivered,"cancel delivered");drain();
        }
        {
            std::cerr<<"transport: length boundary\n";
            Server server;commit.text.assign(2048,L'字');
            check(LearningDispatch({commit,104,4,{}})==DispatchResult::Queued,"core-size text forwarded for explicit broker length status");
            const auto packet=server.read();
            check(packet.find(mansur::to_utf8(commit.text))!=std::string::npos,"2048 UTF16 units forwarded intact");
            check(wait_status(104).status==DeliveryStatus::Delivered,"core-size request delivered");drain();
        }
        commit.text=L"你好";
        {
            std::cerr<<"transport: delayed server ConnectNamedPipe\n";
            const auto before=LearningTestConnectAttempts();
            check(LearningDispatch({commit,108,8,{}})==DispatchResult::Queued,"delayed-connect request queued");
            wait_attempts(before+2);
            Server server(150);const auto packet=server.read();
            check(packet.find("\"revision\":8")!=std::string::npos,"client remains connected until server reads full line");
            check(wait_status(108).status==DeliveryStatus::Delivered,"delayed server connection safely acknowledged");drain();
        }
        {
            std::cerr<<"transport: missing acknowledgement does not replay\n";
            const auto before=LearningTestConnectAttempts();
            Server server;
            check(LearningDispatch({commit,109,9,{}})==DispatchResult::Queued,"lost-ack request queued");
            check(!server.read(false,false).empty(),"request reached server before ack loss");
            check(wait_status(109).status==DeliveryStatus::TimedOut,"missing ack reported unconfirmed");drain();
            check(LearningTestConnectAttempts()==before+1,"missing ack never retransmits written request");
        }
        {
            std::cerr<<"transport: disconnected acknowledgement does not replay\n";
            const auto before=LearningTestConnectAttempts();
            {
                Server server;
                check(LearningDispatch({commit,118,18,{}})==DispatchResult::Queued,"disconnect request queued");
                check(!server.read(false,false).empty(),"complete request reaches disconnecting server");
            }
            check(wait_status(118).status==DeliveryStatus::Unavailable,"disconnect before ack is not delivered");drain();
            check(LearningTestConnectAttempts()==before+1,"disconnect after write never retransmits");
        }
        {
            std::cerr<<"transport: cancel supersedes acknowledgement wait\n";
            {
                Server server;
                check(LearningDispatch({commit,119,19,{}})==DispatchResult::Queued,"learn awaits receipt ack");
                check(!server.read(false,false).empty(),"earlier learn has been written once");
                check(CancelLearning(119)==DispatchResult::Queued,"cancel supersedes written learn awaiting ack");
                Sleep(60);
            }
            Server server;const auto packet=server.read();
            check(packet.find("\"op\":\"cancel\"")!=std::string::npos,"new cancel follows written learn without replaying it");
            check(wait_status(119).status==DeliveryStatus::Delivered,"cancel keeps its receipt and sequence");drain();
        }
        {
            std::cerr<<"transport: incompatible old server fails before any write\n";
            const auto before=LearningTestConnectAttempts();
            Server server(0,true,PIPE_ACCESS_INBOUND);
            check(LearningDispatch({commit,120,20,{}})==DispatchResult::Queued,"old server compatibility failure queued off input thread");
            check(wait_status(120).status==DeliveryStatus::Unavailable,"inbound-only server requires matching backend update");drain();
            check(LearningTestConnectAttempts()==before+1,"access mismatch does not silently downgrade to unsafe delivery");
        }
        {
            std::cerr<<"transport: startup gap\n";
            const auto before=LearningTestConnectAttempts();
            check(LearningDispatch({commit,110,10,{}})==DispatchResult::Queued,"startup gap queued");
            wait_attempts(before+3);
            Server server;const auto packet=server.read();
            check(packet.find("\"revision\":10")!=std::string::npos,"original last request survives short startup gap");
            check(wait_status(110).status==DeliveryStatus::Delivered,"startup gap recovered without another keystroke");drain();
        }
        {
            std::cerr<<"transport: busy broker\n";
            const auto before=LearningTestConnectAttempts();
            {
                Server busy;Client blocker;
                check(LearningDispatch({commit,111,11,{}})==DispatchResult::Queued,"busy broker queued");
                wait_attempts(before+3);
                check(GetLastDeliveryStatus().status==DeliveryStatus::Queued,"temporary pipe busy is not a final failure");
            }
            Server server;const auto packet=server.read();
            check(packet.find("\"revision\":11")!=std::string::npos,"busy listener retry delivered original request");
            check(wait_status(111).status==DeliveryStatus::Delivered,"busy broker recovered");drain();
        }
        {
            std::cerr<<"transport: cancel while busy\n";
            const auto before=LearningTestConnectAttempts();
            {
                Server busy;Client blocker;
                check(LearningDispatch({commit,112,12,{}})==DispatchResult::Queued,"old learn waits on busy broker");
                wait_attempts(before+2);
                check(CancelLearning(112)==DispatchResult::Queued,"new cancel supersedes old waiting learn");
            }
            Server server;const auto packet=server.read();
            check(packet.find("\"op\":\"cancel\"")!=std::string::npos&&packet.find("\"text\"")==std::string::npos,"late connection cannot revive cancelled learn");
            check(wait_status(112).status==DeliveryStatus::Delivered,"superseding cancel delivered");drain();
        }
        {
            std::cerr<<"transport: learn replaces cancel\n";
            const auto before=LearningTestConnectAttempts();
            check(CancelLearning(113)==DispatchResult::Queued,"old cancel waits during gap");wait_attempts(before+2);
            check(LearningDispatch({commit,114,14,{}})==DispatchResult::Queued,"latest learn supersedes old waiting cancel");
            Server server;const auto packet=server.read();
            check(packet.find("\"op\":\"learn\"")!=std::string::npos&&packet.find("\"revision\":14")!=std::string::npos,"old cancel cannot overtake later learn");
            check(wait_status(114).status==DeliveryStatus::Delivered,"last request restored after restart gap");drain();
        }
        {
            std::cerr<<"transport: cancel after connect\n";
            PauseBeforeWrite pause;
            {
                Server connected;
                check(LearningDispatch({commit,115,15,{}})==DispatchResult::Queued,"old learn begins connecting");
                check(WaitForSingleObject(pause.reached,1000)==WAIT_OBJECT_0,"connection established before simulated user event");
                check(CancelLearning(115)==DispatchResult::Queued,"cancel accepted after old pipe connection");
                SetEvent(pause.proceed);
                check(connected.read(true).empty(),"superseded connected request writes no bytes");
            }
            Server server;const auto packet=server.read();
            check(packet.find("\"op\":\"cancel\"")!=std::string::npos,"only latest cancel passes post-connect check");
            check(wait_status(115).status==DeliveryStatus::Delivered,"post-connect superseding request delivered");drain();
        }
        {
            std::cerr<<"transport: partial write\n";
            const auto before=LearningTestConnectAttempts();LearningTestPartialWrite(true);
            {
                Server server;
                check(LearningDispatch({commit,116,16,{}})==DispatchResult::Queued,"partial-write fixture queued");
                const auto packet=server.read();
                check(!packet.empty()&&packet.back()!='\n',"test exercised an incomplete wire packet");
                check(wait_status(116).status==DeliveryStatus::Unavailable,"partial write is ambiguous failure");drain();
            }
            LearningTestPartialWrite(false);
            check(LearningTestConnectAttempts()==before+1,"partially written request is never connected or written twice");
            Server server;
            check(LearningDispatch({commit,117,17,{}})==DispatchResult::Queued,"fresh user request works after partial failure");
            check(server.read().find("\"revision\":17")!=std::string::npos,"next explicit request delivered exactly once");
            check(wait_status(117).status==DeliveryStatus::Delivered,"transport recovers after ambiguous write");drain();
        }
        commit.learn=false;
        check(LearningDispatch({commit,102,3,{}})==DispatchResult::Rejected,"Chinese-only submission not dispatched");
        commit.learn=true;commit.text.assign(2049,L'字');
        check(LearningDispatch({commit,103,4,{}})==DispatchResult::Rejected,"oversized request rejected");
        commit.text=L"你好";LearningTestFailCounter(true);
        check(LearningDispatch({commit,105,5,{}})==DispatchResult::Rejected,"clock failure rejects learn without alternate time");
        check(CancelLearning(105)==DispatchResult::Rejected,"clock failure rejects cancel without alternate time");
        LearningTestFailCounter(false);
        std::cout<<"Learning transport checks passed: confirmed ERROR_NO_DATA race, delayed-connect receipt ACK, absent/lost/disconnected ACK without replay, bounded absent/busy/startup retries, latest learn/cancel before/after connect and ACK wait, no retry after partial write, JSON/caret, QPC order/sender, clock failure, 2048-unit bounds. Private test pipe only; no UI/model/user text.\n";
        return 0;
    } catch(const std::exception& error) {std::cerr<<error.what()<<'\n';drain();return 1;}
}
