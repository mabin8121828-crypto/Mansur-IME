// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "settings.hpp"
#include <windows.h>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
using namespace mansur::win;
void check(bool value,const char* message){if(!value)throw std::runtime_error(message);}
struct Fixture {
    std::filesystem::path root,path,temporary;
    Fixture(){root=std::filesystem::temp_directory_path()/(L"mansur-settings-test-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64()));std::filesystem::create_directory(root);path=root/L"preferences.ini";temporary=root/L"preferences.tmp";SettingsTestPath(path);SettingsTestTick(1000);}
    ~Fixture(){std::error_code ignored;std::filesystem::remove(temporary,ignored);std::filesystem::remove(path,ignored);std::filesystem::remove(root,ignored);}
    void publish(const std::string& bytes){
        {std::ofstream file(temporary,std::ios::binary|std::ios::trunc);file.write(bytes.data(),static_cast<std::streamsize>(bytes.size()));check(file.good(),"fixture JSON-free settings written");}
        check(MoveFileExW(temporary.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=FALSE,"fixture atomic replacement");
    }
};
const std::string header="MansurNextSettings=1\n";
int main(){try{
    Fixture fixture;
    auto missing=ReadNativeSettings(true);
    check(missing.theme==UiTheme::System&&missing.font_points==12&&missing.abbreviation&&missing.auto_remember&&missing.user_lexicon.empty(),"missing shared file uses independent defaults");
    const std::string full="MansurNextSettings=1\r\n\r\nTheme=2\r\nCandidateLayout=1\r\nFontSize=20\r\nAbbreviation=0\r\nFuzzyMask=255\r\nToolbarVisible=1\r\nVoice=bf_emma\r\nSpeedPercent=125\r\nUserLexiconPath=C:\\学习 目录\\merged.mlex\r\nLexiconRevision=7\r\nToolbarX=-2147483648\r\nToolbarY=2147483647\r\nAutoRemember=0\r\n";
    fixture.publish(full);auto all=ReadNativeSettings(true);
    check(all.theme==UiTheme::Dark&&all.layout==CandidateLayout::Vertical&&all.font_points==20&&!all.abbreviation&&!all.auto_remember&&all.fuzzy_mask==255,"all appearance/input fields parsed from shared file");
    check(all.user_lexicon==std::filesystem::path(L"C:\\学习 目录\\merged.mlex")&&all.lexicon_revision==7,"Unicode absolute dictionary path retained");
    check(all.chinese_mode,"shared preference never persists English mode");
    const auto reads=SettingsTestReads();
    for(unsigned i=0;i<100;++i)check(ReadNativeSettings().font_points==20,"repeated key refresh uses cached settings");
    check(SettingsTestReads()==reads,"100 key refreshes perform no extra file reads");
    check(ReadNativeSettings().user_lexicon.empty(),"per-key result does not expose activation dictionary pointer");
    fixture.publish(header+"FontSize=18\nTheme=1\n");SettingsTestTick(1199);
    check(ReadNativeSettings().font_points==20,"cache remains stable before 200 milliseconds");
    SettingsTestTick(1200);check(ReadNativeSettings().font_points==18&&SettingsTestReads()==reads+1,"cache refreshes at 200 milliseconds");
    fixture.publish(header+"FontSize=17\n");
    check(ReadNativeSettings(true).font_points==17&&ReadNativeSettings().auto_remember,"legacy file without auto remember keeps enabled default");
    fixture.publish(header+"FontSize=17\nAutoRemember=0\n");
    check(!ReadNativeSettings(true).auto_remember,"auto remember explicitly disabled without affecting other input fields");
    const auto learning=fixture.root/L"learning-preferences.ini";
    {std::ofstream file(learning,std::ios::binary);file<<"MansurNextLearningSettings=1\nEnglishWritebackMode=0\nSystemSpeechFallback=1\nEnglishSuggestions=1\n";}
    check(ReadNativeSettings(true).english_suggestions,"English suggestions read separate opt-in file without changing legacy native protocol");
    {std::ofstream file(learning,std::ios::binary|std::ios::trunc);file<<"MansurNextLearningSettings=1\nEnglishSuggestions=2\n";}
    check(ReadNativeSettings(true).english_suggestions,"invalid English setting retains last valid opt-in state");
    {std::ofstream file(learning,std::ios::binary|std::ios::trunc);file<<"MansurNextLearningSettings=1\nEnglishSuggestions=0\n";}
    check(!ReadNativeSettings(true).english_suggestions,"English suggestions can be disabled independently");
    std::filesystem::remove(learning);
    std::vector<std::string> invalid={
        "", "MansurNextSettings=2\nFontSize=16\n", "\n"+header, header+"Theme=1\nTheme=2\n",
        header+"Unknown=1\n",header+"ChineseMode=0\n",header+"FontSize=21\n",header+"FontSize=+12\n",
        header+"FontSize= 12\n",header+"FontSize=12 \n",header+"FontSize=2147483648\n",header+"Theme=-1\n",
        header+"ToolbarX=-2147483649\n",header+"LexiconRevision=-1\n",header+"Voice=arbitrary\n",
        header+"Abbreviation=2\n",header+"AutoRemember=2\n",header+"AutoRemember=-1\n",header+"AutoRemember=true\n",header+"AutoRemember=0\nAutoRemember=1\n",header+"CandidateLayout=2\n",header+"FuzzyMask=256\n",header+"SpeedPercent=74\n",
        header+"ToolbarVisible=2\n",header+"Malformed\n",header+"Theme=1\rFontSize=16\n",
        header+"UserLexiconPath=C:relative.mlex\n",header+"UserLexiconPath=\\rootless.mlex\n",
        header+"UserLexiconPath=\\\\server\\\n",header+"UserLexiconPath=\\\\?\\C:\\data.mlex\n",
        std::string("\xEF\xBB\xBF")+header,header+"Voice="+std::string("\xC0\xAF")+"\n",
        header+"UserLexiconPath=C:\\"+std::string("\xED\xA0\x80")+"\n",header+std::string("Theme=1\0\n",9)
    };
    for(const auto& bytes:invalid){fixture.publish(bytes);auto cached=ReadNativeSettings(true);check(cached.font_points==17&&!cached.auto_remember,"invalid whole file preserves last valid snapshot including disabled learning");}
    SettingsTestPath(fixture.path);auto reset=ReadNativeSettings(true);check(reset.font_points==12&&reset.auto_remember,"invalid file with no history uses defaults");
    auto exact=header+"FontSize=19\n";exact.resize(32768,'\n');fixture.publish(exact);
    check(ReadNativeSettings(true).font_points==19,"32768-byte file is accepted");
    fixture.publish(exact+"\n");check(ReadNativeSettings(true).font_points==19,"oversize file keeps valid settings");
    fixture.publish(header+"FontSize=16\nUserLexiconPath=\\\\server\\share\\词库.mlex\nToolbarX=-0\n");
    check(ReadNativeSettings(true).font_points==16&&!ReadNativeSettings(true).user_lexicon.empty(),"complete UNC metadata and negative zero parse without dictionary IO");
    fixture.publish(header+"UserLexiconPath=\n");check(ReadNativeSettings(true).user_lexicon.empty(),"empty dictionary preference is valid");
    fixture.publish(header+"FontSize=18\n");check(ReadNativeSettings(true).font_points==18,"restore fixture before delete");
    check(std::filesystem::remove(fixture.path),"fixture file removed");check(ReadNativeSettings(true).font_points==12,"missing file clears stale custom settings to defaults");
    fixture.publish(header+"AutoRemember=1\n");check(ReadNativeSettings(true).auto_remember,"auto remember can be explicitly re-enabled");
    std::cout<<"Shared preferences passed: 13 keys, Unicode paths, 200ms cache, activation refresh, "<<invalid.size()<<" malformed inputs, byte limit and missing/bad-file recovery. Isolated path only; no real settings written.\n";
    return 0;
}catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}}
