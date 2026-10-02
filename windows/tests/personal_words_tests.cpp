// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "personal_words.hpp"
#include "module.hpp"
#include <objbase.h>
#include <algorithm>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <thread>
#include <vector>

namespace mansur::win {
HINSTANCE module_instance=nullptr;
std::atomic<long> live_objects{0},server_locks{0};
}
using namespace mansur;
using namespace mansur::win;
namespace {
void check(bool value,const char* message){if(!value)throw std::runtime_error(message);}
std::wstring unique() {
    GUID id{};wchar_t text[40]{};check(SUCCEEDED(CoCreateGuid(&id))&&StringFromGUID2(id,text,40),"test identity");
    std::wstring result(text+1,36);return result;
}
void write_file(const std::filesystem::path& path,const std::string& data) {
    std::ofstream file(path,std::ios::binary|std::ios::trunc);file.write(data.data(),static_cast<std::streamsize>(data.size()));check(static_cast<bool>(file),"fixture file write");
}
std::string read_file(const std::filesystem::path& path) {
    std::ifstream file(path,std::ios::binary);check(static_cast<bool>(file),"fixture file read");
    return {std::istreambuf_iterator<char>(file),std::istreambuf_iterator<char>()};
}
struct Fixture {
    std::wstring name=unique();
    std::filesystem::path directory=std::filesystem::temp_directory_path()/(L"mansur-personal-"+name);
    std::filesystem::path file=directory/L"learned-words.tsv";
    Fixture(){std::filesystem::create_directory(directory);configure();}
    void configure(){check(PersonalWordsTestWait(),"previous worker completed");check(PersonalWordsTestConfigure(file,name),"private store configured");}
    ~Fixture() {
        if(!PersonalWordsTestWait())return;
        // Only the unique directory created by this fixture is eligible.
        if(directory.is_absolute()&&directory.parent_path()==std::filesystem::temp_directory_path()&&directory.filename()==L"mansur-personal-"+name) {
            std::error_code error;std::filesystem::remove_all(directory,error);
        }
    }
};
std::shared_ptr<const PersonalLexicon> loaded() {
    PersonalWordsTestRefresh();GetPersonalLexicon();check(PersonalWordsTestWait(),"background store idle");
    return GetPersonalLexicon();
}
std::uint32_t uses(const std::shared_ptr<const PersonalLexicon>& lexicon,const std::wstring& text,const std::string& syllables) {
    if(lexicon)for(const auto& word:lexicon->words())if(word.text==text&&word.syllables==syllables)return word.uses;
    return 0;
}
void save(const std::vector<PersonalWord>& words) {
    check(RememberPersonalWords(words),"usage batch accepted");check(PersonalWordsTestWait(),"usage batch worker completed");
    const auto status=GetPersonalWordsDiagnostics();check(status.state==PersonalWordsState::Ready&&status.pending_words==0,"usage batch persisted");
}
struct Child {
    PROCESS_INFORMATION process{};
    Child(const std::filesystem::path& executable,const std::wstring& arguments) {
        std::wstring command=L"\""+executable.native()+L"\" "+arguments;
        STARTUPINFOW startup{};startup.cb=sizeof(startup);
        check(CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&process)!=FALSE,"private child process created");
        CloseHandle(process.hThread);process.hThread=nullptr;
    }
    Child(const Child&)=delete;Child& operator=(const Child&)=delete;
    ~Child(){if(process.hProcess)CloseHandle(process.hProcess);}
    void wait(){check(WaitForSingleObject(process.hProcess,20000)==WAIT_OBJECT_0,"private child completed");DWORD code=STILL_ACTIVE;check(GetExitCodeProcess(process.hProcess,&code)&&code==0,"private child actual exit success");}
};
std::filesystem::path executable_path() {
    std::vector<wchar_t> path(32768);auto count=GetModuleFileNameW(nullptr,path.data(),static_cast<DWORD>(path.size()));
    check(count&&count<path.size(),"self executable path");return std::wstring(path.data(),count);
}
std::wstring quote(const std::filesystem::path& path){return L"\""+path.native()+L"\"";}
int child_main(int argc,wchar_t** argv) {
    check(argc>=5,"child arguments");
    check(PersonalWordsTestConfigure(argv[2],argv[3]),"child private store");
    if(std::wstring(argv[1])==L"--merge") {
        const auto amount=static_cast<std::uint32_t>(std::stoul(argv[4]));
        check(RememberPersonalWords({{L"九班","jiu ban",amount},{L"你好","ni hao",1}}),"child increments accepted");
        // A peer can hold the file lock for one burst; retry only retained work.
        for(unsigned retry=0;retry<8;++retry) {
            check(PersonalWordsTestWait(),"child flush bounded");
            if(GetPersonalWordsDiagnostics().pending_words==0)return 0;
            Sleep(30);PersonalWordsTestRefresh();GetPersonalLexicon();
        }
        check(false,"child deltas persisted after bounded retry");
    }else if(std::wstring(argv[1])==L"--read"||std::wstring(argv[1])==L"--rank") {
        auto lexicon=loaded();check(uses(lexicon,L"九班","jiu ban")==std::stoul(argv[4]),"fresh process reads merged uses");
        if(std::wstring(argv[1])==L"--rank") {
            check(argc==6,"fresh rank dictionary argument");auto dictionary=std::make_shared<Dictionary>();dictionary->load(argv[5]);
            DraftSession draft(dictionary);draft.set_personal_lexicon(lexicon);
            for(char c:std::string("jiuban"))check(draft.key({KeyKind::Letter,static_cast<wchar_t>(c),1000,false}).consumed,"fresh process pinyin");
            check(!draft.candidates().empty()&&draft.candidates().front().text==L"九班","fresh process ranks persisted word first");
        }
        return 0;
    }
    return 2;
}
void storage_tests(const std::filesystem::path& peer) {
    {
        Fixture f;LARGE_INTEGER frequency{},start{},end{};QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&start);
        auto first=GetPersonalLexicon();QueryPerformanceCounter(&end);
        const double cold=1e6*(end.QuadPart-start.QuadPart)/frequency.QuadPart;
        check(PersonalWordsTestWait(),"cold async load completed");auto empty=GetPersonalLexicon();check(empty&&empty->words().empty(),"missing file supplies empty personal index");
        QueryPerformanceCounter(&start);for(int i=0;i<1000;++i)GetPersonalLexicon();QueryPerformanceCounter(&end);
        std::cout<<"Get latency cold_us="<<cold<<" steady_average_us="<<(1e6*(end.QuadPart-start.QuadPart)/frequency.QuadPart/1000)<<'\n';
        check(PersonalWordsTestOperations().caller_thread_io==0,"cold/steady Get never performs file IO on input caller");
        save({{L"九班","jiu ban",2}});save({{L"九班","jiu ban",3},{L"你好","ni hao",1}});
        auto original=GetPersonalLexicon();check(uses(original,L"九班","jiu ban")==5&&uses(original,L"你好","ni hao")==1,"usage increments merge exactly");
        auto unchanged=loaded();check(unchanged==original,"unchanged file does not replace index identity");
        const auto bytes=read_file(f.file);check(bytes.rfind("MansurNextPersonalWords=1\n",0)==0&&bytes.find("\xEF\xBB\xBF")==std::string::npos,"portable UTF8 file has fixed header and no BOM");
        f.configure();check(uses(loaded(),L"九班","jiu ban")==5,"memory reset reloads persisted usage");
        check(PersonalWordsTestOperations().caller_thread_io==0,"save and restart reads remain background only");
    }
    {
        Fixture f;save({{L"九班","jiu ban",999999}});save({{L"九班","jiu ban",20}});
        check(uses(GetPersonalLexicon(),L"九班","jiu ban")==1000000,"frequency addition saturates without wrapping");
    }
    {
        Fixture f;
        for(const auto& invalid:std::vector<PersonalWord>{{L"九班","jiuban",1},{L"九班","jiu  ban",1},{L"九班","jiu xxx",1},{L"九班","jiu",1},{L"hello","he lo",1},{L"九班","jiu ban",0},{L"九班","jiu ban",1000001}})
            check(!RememberPersonalWords({invalid}),"invalid personal word rejected before queue");
        check(GetPersonalWordsDiagnostics().rejected_batches==7&&!std::filesystem::exists(f.file),"rejected batches recorded without persistence");
    }
    {
        Fixture f;save({{L"九班","jiu ban",3}});auto previous=GetPersonalLexicon();
        const std::string broken="MansurNextPersonalWords=1\n\xff\tjiu ban\t1\n";write_file(f.file,broken);
        check(RememberPersonalWords({{L"九班","jiu ban",1}})&&PersonalWordsTestWait(),"broken file can retain a new in-memory increment");
        auto status=GetPersonalWordsDiagnostics();check(status.state==PersonalWordsState::InvalidData&&status.pending_words==1&&read_file(f.file)==broken,"corrupt bytes preserved with visible pending failure");
        check(GetPersonalLexicon()==previous,"broken file preserves last valid index");
        write_file(f.file,"MansurNextPersonalWords=1\n"+to_utf8(L"九班")+"\tjiu ban\t3\n");
        check(uses(loaded(),L"九班","jiu ban")==4&&GetPersonalWordsDiagnostics().pending_words==0,"repair permits exactly one retained increment");
    }
    for(const auto& data:std::vector<std::string>{
        "MansurNextPersonalWords=1\n"+to_utf8(L"九班")+"\tjiu ban\t1\n"+to_utf8(L"九班")+"\tjiu ban\t2\n",
        "MansurNextPersonalWords=1\n\n",
        "\xEF\xBB\xBFMansurNextPersonalWords=1\n",
        "MansurNextPersonalWords=1\n"+to_utf8(L"九班")+"\tjiu ban\t01\n",
        std::string(4*1024*1024+1,'x')}) {
        Fixture f;write_file(f.file,data);GetPersonalLexicon();check(PersonalWordsTestWait(),"malformed load bounded");
        check(GetPersonalWordsDiagnostics().state==PersonalWordsState::InvalidData&&read_file(f.file)==data,"malformed/oversized files are preserved");
    }
    {
        Fixture f;write_file(f.file,"MansurNextPersonalWords=1\r\n"+to_utf8(L"九班")+"\tjiu ban\t2\r\n");
        check(uses(loaded(),L"九班","jiu ban")==2,"CRLF input is portable across architectures");
    }
    {
        Fixture f;save({{L"九班","jiu ban",2}});const auto original=read_file(f.file);
        check(SetFileAttributesW(f.file.c_str(),FILE_ATTRIBUTE_READONLY)!=FALSE,"make temporary destination unwritable");
        check(RememberPersonalWords({{L"九班","jiu ban",1}})&&PersonalWordsTestWait(),"unwritable destination handled in worker");
        check(GetPersonalWordsDiagnostics().state==PersonalWordsState::IoError&&GetPersonalWordsDiagnostics().pending_words==1&&read_file(f.file)==original,"failed atomic replace preserves original and pending delta");
        check(SetFileAttributesW(f.file.c_str(),FILE_ATTRIBUTE_NORMAL)!=FALSE,"restore temporary destination permission");
        check(uses(loaded(),L"九班","jiu ban")==3,"retry after write failure applies increment once");
        for(const auto& entry:std::filesystem::directory_iterator(f.directory))check(entry.path().extension()!=L".tmp","failed writes leave no incomplete temporary file");
    }
    {
        Fixture f;const auto blocked=f.directory/L"not-a-directory";write_file(blocked,"fixed test fixture");
        check(PersonalWordsTestConfigure(blocked/L"learned-words.tsv",f.name),"blocked path configured only in test");
        check(RememberPersonalWords({{L"九班","jiu ban",1}})&&PersonalWordsTestWait(),"invalid directory does not block caller");
        check(GetPersonalWordsDiagnostics().state==PersonalWordsState::IoError&&read_file(blocked)=="fixed test fixture","unwritable directory preserves its existing object");
    }
    {
        Fixture f;const auto lock_path=std::filesystem::path(f.file.native()+L".lock");
        HANDLE exclusive=CreateFileW(lock_path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
        check(exclusive!=INVALID_HANDLE_VALUE,"test peer holds shared file lock");
        const auto started=GetTickCount64();check(RememberPersonalWords({{L"九班","jiu ban",1}}),"busy file still accepts nonblocking enqueue");
        const auto enqueue_ms=GetTickCount64()-started;
        check(PersonalWordsTestWait()&&GetPersonalWordsDiagnostics().state==PersonalWordsState::Busy&&GetPersonalWordsDiagnostics().pending_words==1,"file lock wait ends with retained pending status");
        CloseHandle(exclusive);std::cout<<"Busy-lock enqueue_ms="<<enqueue_ms<<'\n';
        check(uses(loaded(),L"九班","jiu ban")==1,"peer releasing lock permits original increment");
    }
    {
        Fixture f;const auto lock_path=std::filesystem::path(f.file.native()+L".lock");
        HANDLE exclusive=CreateFileW(lock_path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);check(exclusive!=INVALID_HANDLE_VALUE,"queue bound lock");
        std::vector<PersonalWord> full(10000,{L"九班","jiu ban",1});
        check(RememberPersonalWords(full)&&!RememberPersonalWords({{L"你好","ni hao",1}}),"pending word count is bounded without dropping accepted batch");
        check(PersonalWordsTestWait(),"bounded pending worker returned");CloseHandle(exclusive);
        check(uses(loaded(),L"九班","jiu ban")==10000&&GetPersonalWordsDiagnostics().rejected_batches==1,"bounded queue preserves all accepted increments");
    }
    {
        Fixture f;std::string file="MansurNextPersonalWords=1\n";
        for(unsigned i=0;i<10000;++i) {
            const std::wstring word{static_cast<wchar_t>(0x4e00+i/100),static_cast<wchar_t>(0x4e00+i%100)};
            file+=to_utf8(word)+"\tni hao\t1\n";
        }
        write_file(f.file,file);auto snapshot=loaded();check(snapshot&&snapshot->words().size()==10000,"maximum on-disk index loads");
        check(RememberPersonalWords({{L"九班","jiu ban",1}})&&PersonalWordsTestWait(),"over-capacity addition handled in background");
        check(GetPersonalWordsDiagnostics().state==PersonalWordsState::Limit&&GetPersonalWordsDiagnostics().pending_words==0&&GetPersonalWordsDiagnostics().skipped_new_words==1&&read_file(f.file)==file,"10001st entry is explicitly skipped without eviction or retained queue blockage");
        check(GetPersonalLexicon()==snapshot,"capacity error leaves previous usable index");
        const std::wstring known{static_cast<wchar_t>(0x4e00),static_cast<wchar_t>(0x4e00)};
        check(RememberPersonalWords({{known,"ni hao",3},{L"九班","jiu ban",2}})&&PersonalWordsTestWait(),"full store accepts mixed existing/new batch");
        auto mixed=GetPersonalWordsDiagnostics();
        check(mixed.state==PersonalWordsState::Limit&&mixed.pending_words==0&&mixed.skipped_new_words==2&&uses(GetPersonalLexicon(),known,"ni hao")==4&&uses(GetPersonalLexicon(),L"九班","jiu ban")==0,"existing increment is persisted once while full-store new word is skipped");
        save({{known,"ni hao",2}});
        check(uses(GetPersonalLexicon(),known,"ni hao")==6&&GetPersonalWordsDiagnostics().skipped_new_words==2,"subsequent known word updates remain unblocked without replaying prior mixed batch");
        f.configure();check(uses(loaded(),known,"ni hao")==6&&uses(GetPersonalLexicon(),L"九班","jiu ban")==0,"capacity handling survives cold reload with exact frequencies");
    }
    {
        Fixture f;auto self=executable_path();
        const auto common=quote(f.file)+L" "+f.name;
        Child a(self,L"--merge "+common+L" 17");Child b(peer.empty()?self:peer,L"--merge "+common+L" 23");
        a.wait();b.wait();check(uses(loaded(),L"九班","jiu ban")==40&&uses(GetPersonalLexicon(),L"你好","ni hao")==2,"concurrent processes merge without last-writer loss");
        Child restart(peer.empty()?self:peer,L"--read "+common+L" 40");restart.wait();
        std::cout<<(peer.empty()?"Same-architecture":"Cross-architecture")<<" concurrent merge and fresh-process reload PASS\n";
    }
    {
        Fixture f;std::atomic<unsigned> accepted{0};std::vector<std::thread> writers;
        for(unsigned thread=0;thread<4;++thread)writers.emplace_back([&]{for(unsigned n=0;n<40;++n)if(RememberPersonalWords({{L"九班","jiu ban",1}}))++accepted;});
        for(auto& writer:writers)writer.join();check(PersonalWordsTestWait(),"parallel producers drain");
        auto snapshot=loaded();check(accepted==160&&uses(snapshot,L"九班","jiu ban")==160,"SLIST producers do not lose batches to cache lock contention");
        check(GetPersonalWordsDiagnostics().rejected_batches==0&&PersonalWordsTestOperations().caller_thread_io==0,"ordinary concurrent enqueue has no silent rejection or caller IO");
    }
}
void letters(DraftSession& draft,const std::string& text) {
    for(char c:text)check(draft.key({KeyKind::Letter,static_cast<wchar_t>(c),1000,false}).consumed,"fixed sample pinyin accepted");
}
void choose(DraftSession& draft,const std::wstring& text) {
    const auto& candidates=draft.candidates();auto found=std::find_if(candidates.begin(),candidates.end(),[&](const Candidate& candidate){return candidate.text==text;});
    check(found!=candidates.end(),"fixed sample candidate exists");const auto index=static_cast<std::size_t>(found-candidates.begin());
    while(draft.selected()/9<index/9)check(draft.key({KeyKind::PageDown,0,1000,false}).consumed,"candidate page navigates");
    while(draft.selected()/9>index/9)check(draft.key({KeyKind::PageUp,0,1000,false}).consumed,"candidate previous page navigates");
    check(draft.key({KeyKind::Digit,static_cast<wchar_t>(L'1'+index%9),1000,false}).consumed,"number selects complete candidate");
}
void dictionary_test(const std::filesystem::path& path) {
    Fixture f;auto dictionary=std::make_shared<Dictionary>();dictionary->load(path);
    DraftSession draft(dictionary);
    for(unsigned n=0;n<3;++n) {
        letters(draft,"jiu");choose(draft,L"九");letters(draft,"ban");choose(draft,L"班");
        check(draft.key({KeyKind::Punctuation,L'，',1000,false}).consumed,"punctuation ends one observation group");
    }
    letters(draft,"jiuban");
    check(!draft.candidates().empty()&&draft.candidates().front().text==L"九班","repeated explicit selections promote composed word within session");
    choose(draft,L"九班");const auto prepared=draft.key({KeyKind::Enter,0,1000,false});
    check(prepared.commit&&prepared.commit->text==L"九班，九班，九班，九班"&&!prepared.commit->personal_words.empty(),"Enter prepares complete Chinese and usage delta");
    check(draft.acknowledge(prepared.commit->id,CommitResult::Written),"fixed sample explicit Written acknowledgement");
    save(prepared.commit->personal_words);auto personal=GetPersonalLexicon();
    DraftSession fresh(dictionary);fresh.set_personal_lexicon(personal);letters(fresh,"jiuban");
    check(!fresh.candidates().empty()&&fresh.candidates().front().text==L"九班","fresh session ranks saved composed word first");
    const auto expected=uses(personal,L"九班","jiu ban");check(expected>=3,"composition counts persisted");
    Child restart(executable_path(),L"--rank "+quote(f.file)+L" "+f.name+L" "+std::to_wstring(expected)+L" "+quote(path));restart.wait();
    // The child above proves independent process deserialization and ranking.
    // Rebuild from a cold store here too, keeping the check in both processes.
    f.configure();DraftSession reloaded(dictionary);reloaded.set_personal_lexicon(loaded());letters(reloaded,"jiuban");
    check(!reloaded.candidates().empty()&&reloaded.candidates().front().text==L"九班","cold persisted store also promotes exact full pinyin");
    std::cout<<"Production dictionary + explicit jiu/ban selections x3 + Written + store + cold-session ranking PASS\n";
}
}
int wmain(int argc,wchar_t** argv) {
    try {
        if(argc>1&&(std::wstring(argv[1])==L"--merge"||std::wstring(argv[1])==L"--read"||std::wstring(argv[1])==L"--rank"))return child_main(argc,argv);
        std::filesystem::path peer,dictionary;
        for(int i=1;i<argc;++i) {
            const std::wstring option=argv[i];check(i+1<argc,"option value");
            if(option==L"--peer")peer=argv[++i];else if(option==L"--dictionary")dictionary=argv[++i];else check(false,"unknown test option");
        }
        storage_tests(peer);if(!dictionary.empty())dictionary_test(dictionary);
        check(PersonalWordsTestWait(),"final workers released");
        std::cout<<"Personal word store validation, bounds, atomic failure, concurrency and restart tests PASS (isolated paths; no companion or UI).\n";return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
}
