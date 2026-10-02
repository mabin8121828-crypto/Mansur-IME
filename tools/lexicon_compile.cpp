// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "mansur/core.hpp"
#include <atomic>
#include <chrono>
#include <iostream>
#ifdef _WIN32
#include <windows.h>
#endif
namespace {
struct CompileError {const char* code;std::size_t line=0;};
struct Staging {
    std::filesystem::path directory,file;
    ~Staging(){std::error_code error;if(!file.empty())std::filesystem::remove(file,error);if(!directory.empty())std::filesystem::remove(directory,error);}
};
bool same_path(const std::filesystem::path& a,const std::filesystem::path& b) {
    const auto first=std::filesystem::absolute(a).lexically_normal();
    const auto second=std::filesystem::absolute(b).lexically_normal();
#ifdef _WIN32
    return _wcsicmp(first.c_str(),second.c_str())==0;
#else
    return first==second;
#endif
}
int compile(const std::vector<std::filesystem::path>& args) {
    try {
        const bool merge=args.size()==5&&args[1]=="--merge";
        if(!merge&&args.size()!=3)throw CompileError{"usage: mansur_lexicon_compile input.tsv output.mlex | --merge base.mlex user.tsv output.mlex"};
        const auto& base=args[merge?2:1];const auto& output=args[merge?4:2];
        if(same_path(base,output)||(merge&&same_path(args[3],output)))throw CompileError{"output_conflicts_with_input"};
        mansur::Dictionary dictionary;
        if(merge) {
            const auto& user=args[3];
            if(base.extension()!=L".mlex"||user.extension()!=L".tsv")throw CompileError{"invalid_input_format"};
            if(std::filesystem::file_size(user)>4*1024*1024)throw CompileError{"user_dictionary_too_large"};
            mansur::Dictionary checked;
            const auto report=checked.load(user,false);
            if(report.rejected)throw CompileError{"invalid_user_row",report.first_rejected_line};
            if(checked.size()>10000)throw CompileError{"too_many_user_entries"};
            dictionary.load(base);
            const auto merged=dictionary.load(user,false);
            if(merged.rejected)throw CompileError{"invalid_user_row",merged.first_rejected_line};
        } else {
            const auto report=dictionary.load(base);
            if(report.rejected)throw CompileError{"invalid_dictionary_row",report.first_rejected_line};
        }
        const auto parent=std::filesystem::absolute(output).parent_path();
        if(!std::filesystem::is_directory(parent))throw CompileError{"output_directory_unavailable"};
        Staging staging;
        static std::atomic<unsigned> serial{0};
        const auto stamp=std::chrono::steady_clock::now().time_since_epoch().count();
        for(unsigned attempt=0;attempt<16;++attempt) {
            const auto trial=parent/(L".mansur-compile-"+std::to_wstring(stamp)+L"-"+std::to_wstring(++serial));
            if(std::filesystem::create_directory(trial)){staging.directory=trial;break;}
        }
        if(staging.directory.empty())throw CompileError{"staging_unavailable"};
        staging.file=staging.directory/L"custom.mlex";
        dictionary.save_binary(staging.file);
        // Same-volume atomic publication: existing output survives every error.
#ifdef _WIN32
        if(!MoveFileExW(staging.file.c_str(),std::filesystem::absolute(output).c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))
            throw CompileError{"publish_failed"};
#else
        std::filesystem::rename(staging.file,output);
#endif
        std::cout<<"compiled_entries="<<dictionary.size()<<'\n';return 0;
    } catch(const CompileError& error) {
        std::cerr<<error.code;if(error.line)std::cerr<<" line="<<error.line;std::cerr<<'\n';return 1;
    } catch(const mansur::DictionaryFormatError& error) {
        std::cerr<<"invalid_dictionary_row";if(error.line)std::cerr<<" line="<<error.line;std::cerr<<'\n';return 1;
    } catch(...) {
        // Runtime/filesystem exceptions may contain private paths or input text.
        std::cerr<<"dictionary_compile_failed\n";return 1;
    }
}
}
#ifdef _WIN32
int wmain(int argc,wchar_t** argv) {
#else
int main(int argc,char** argv) {
#endif
    try {return compile(std::vector<std::filesystem::path>(argv,argv+argc));}
    catch(...){std::cerr<<"dictionary_compile_failed\n";return 1;}
}
