// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "mansur/core.hpp"
#include <chrono>
#include <iostream>
#include <iomanip>
int main(int argc,char** argv) {
    if(argc<3) { std::cerr<<"Usage: mansur_core_probe dictionary.tsv pinyin [pinyin...]\n"; return 2; }
    try {
        auto d=std::make_shared<mansur::Dictionary>();
        const auto start=std::chrono::steady_clock::now(); const auto report=d->load(argv[1]);
        auto ms=[](auto begin){return std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-begin).count();};
        std::cout<<std::fixed<<std::setprecision(3)<<"dictionary_entries="<<d->size()<<" rejected="<<report.rejected<<" load_ms="<<ms(start)<<'\n';
        for(int i=2;i<argc;++i) {
            const auto begin=std::chrono::steady_clock::now(); const auto results=d->suggest(argv[i]); const auto elapsed=ms(begin);
            std::cout<<"pinyin="<<argv[i]<<" query_ms="<<elapsed<<'\n';
            for(std::size_t j=0;j<results.size();++j)
                std::cout<<j+1<<"\t"<<mansur::to_utf8(results[j].text)<<"\tconsumed="<<results[j].consumed<<'\n';
        }
    } catch(const std::exception& e) { std::cerr<<"Error: "<<e.what()<<'\n'; return 1; }
    return 0;
}
