// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "mansur/core.hpp"
#include <chrono>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <functional>
#include <cstdlib>
#include <new>
#include <algorithm>
namespace allocation_fault { thread_local long remaining=-1; }
void* operator new(std::size_t size) {
    if(allocation_fault::remaining==0) throw std::bad_alloc();
    if(allocation_fault::remaining>0) --allocation_fault::remaining;
    if(auto p=std::malloc(size?size:1)) return p;
    throw std::bad_alloc();
}
void* operator new[](std::size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p,std::size_t) noexcept { std::free(p); }
void operator delete[](void* p,std::size_t) noexcept { std::free(p); }
using namespace mansur;
namespace {
void require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
struct Fixture {
    std::filesystem::path path;
    Fixture(std::string_view contents,std::string_view extension=".tsv") {
        static unsigned serial=0;
        path=std::filesystem::temp_directory_path()/
          ("mansur-core-"+std::to_string(std::chrono::steady_clock::now().time_since_epoch().count())+"-"+std::to_string(++serial)+std::string(extension));
        std::ofstream out(path,std::ios::binary); out<<contents;
        if(!out) throw std::runtime_error("fixture write failed");
    }
    ~Fixture() { std::error_code error; std::filesystem::remove(path,error); }
};
std::shared_ptr<Dictionary> dictionary() {
    Fixture fixture("ni\t你\t200\tni\nhao\t好\t300\thao\nnihao\t你好\t100\tni hao\n"
                    "wo\t我\t500\two\ndaole\t到了\t100\tdao le\njia\t家\t100\tjia\n"
                    "xian\t先\t400\txian\nxian\t西安\t80\txi an\nxi\t西\t100\txi\nan\t安\t100\tan\n"
                    "nv\t女\t100\tnv\nlv\t绿\t100\tlv\nhe\t𠀀\t1\the\n");
    auto d=std::make_shared<Dictionary>(); d->load(fixture.path); return d;
}
void type(DraftSession& s,std::string_view p) {
    for(char c:p) require(s.key({KeyKind::Letter,static_cast<wchar_t>(c)}).consumed,"letter not consumed");
}
void choose(DraftSession& s) { require(s.key({KeyKind::Space,0,1}).consumed,"selection space passed through"); }
void literal(DraftSession& s,std::string_view text) {
    for(char c:text)require(s.key({KeyKind::Literal,static_cast<wchar_t>(static_cast<unsigned char>(c))}).consumed,"literal not consumed");
}
Commit confirm_english(DraftSession& s,std::uint64_t start=100) {
    const auto first=s.key({KeyKind::EnglishSpace,0,start});
    const auto second=s.key({KeyKind::EnglishSpace,0,start+100});
    const auto third=s.key({KeyKind::EnglishSpace,0,start+200});
    require(!first.commit&&!second.commit&&third.commit.has_value(),"English triple space did not commit exactly once");
    return *third.commit;
}
Commit confirm(DraftSession& s,std::uint64_t start=100) {
    auto first=s.key({KeyKind::Space,0,start});
    auto second=s.key({KeyKind::Space,0,start+100});
    auto third=s.key({KeyKind::Space,0,start+200});
    require(!first.commit && !second.commit && third.commit.has_value(),"triple space did not commit exactly once");
    return *third.commit;
}
bool includes(const std::vector<Candidate>& list,std::wstring_view value) {
    return std::any_of(list.begin(),list.end(),[&](const auto& candidate){return candidate.text==value;});
}
std::shared_ptr<Dictionary> personal_fixture() {
    Fixture source("jiu\t久\t1000\tjiu\njiu\t酒\t800\tjiu\njiu\t九\t20\tjiu\n"
                   "ban\t般\t1000\tban\nban\t班\t20\tban\njiuban\t旧版\t800\tjiu ban\n"
                   "jiuban\t旧班\t700\tjiu ban\njiuban\t酒伴\t500\tjiu ban\n"
                   "xuesheng\t学生\t100\txue sheng\nwan\t玩\t100\twan\nwoaini\t我爱你\t20\two ai ni\n");
    auto d=std::make_shared<Dictionary>();d->load(source.path);return d;
}
std::size_t candidate_position(const DraftSession& session,std::wstring_view text) {
    const auto& values=session.candidates();
    const auto found=std::find_if(values.begin(),values.end(),[&](const auto& value){return value.text==text;});
    return static_cast<std::size_t>(found-values.begin());
}
bool exact_candidate(const DraftSession& session,std::wstring_view text) {
    const auto position=candidate_position(session,text);
    return position<session.candidates().size()&&session.candidates()[position].kind==CandidateKind::Exact;
}
void select_word(DraftSession& session,std::wstring_view word) {
    const auto position=candidate_position(session,word);
    require(position<session.candidates().size(),"required selection missing");
    while(session.selected()!=position)session.key({KeyKind::Down});
    choose(session);
}
std::uint32_t observations(const Commit& commit,std::wstring_view text,std::string_view syllables) {
    for(const auto& word:commit.personal_words)if(word.text==text&&word.syllables==syllables)return word.uses;
    return 0;
}
}
int main(int argc,char** argv) {
    if(argc==3&&std::string_view(argv[1])=="--candidate-policy") {
        try {
            auto d=std::make_shared<Dictionary>();d->load(argv[2]);
            auto personal=std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{
                {L"今天","jin tian",3},{L"今天不玩","jin tian bu wan",1000000},
                {L"今天不玩魔兽世界","jin tian bu wan mo shou shi jie",1000000},
                {L"今天的天气怎么样","jin tian de tian qi zen me yang",1000000}});
            for(const auto query:{"jint","jintian","jt"}) {
                DraftSession s(d);s.set_personal_lexicon(personal);type(s,query);
                require(candidate_position(s,L"今天")<9,"ordinary today word missing from first page");
                for(const auto word:{L"今天不玩",L"今天不玩魔兽世界",L"今天的天气怎么样"})
                    require(!includes(s.candidates(),word),"untyped sentence predicted from today spelling");
            }
            DraftSession long_word(d);long_word.set_personal_lexicon(personal);
            type(long_word,"jintianbuwanmoshoushijie");
            require(exact_candidate(long_word,L"今天不玩魔兽世界"),"previously saved exact word became inaccessible");
            std::cout<<"CANDIDATE_POLICY_PASS scope=production_dictionary_with_fixed_synthetic_personal_entries; no_user_dictionary_modified\n";
            return 0;
        }catch(const std::exception& e){std::cerr<<"CANDIDATE_POLICY_FAILED: "<<e.what()<<'\n';return 1;}
    }
    if(argc==3&&std::string_view(argv[1])=="--performance") {
        try {
            const auto load_start=std::chrono::steady_clock::now();
            auto d=std::make_shared<Dictionary>();const auto report=d->load(argv[2]);
            auto ms=[](auto start){return std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count();};
            std::cout<<"PERF entries="<<d->size()<<" rejected="<<report.rejected<<" load_ms="<<ms(load_start)<<'\n';
            for(const unsigned fuzzy:{0u,255u}) {
                std::vector<double> timings;
                for(const auto input:{"nihao","wan","nh","nhao","w' ai'n","wodaojiayihougeinidadianhua"}) {
                    std::string spelling=input;spelling.erase(std::remove(spelling.begin(),spelling.end(),' '),spelling.end());
                    DraftSession draft(d);draft.set_options({true,fuzzy});
                    for(char c:spelling) {const auto start=std::chrono::steady_clock::now();draft.key({KeyKind::Letter,static_cast<wchar_t>(c)});timings.push_back(ms(start));}
                    std::cout<<"PERF mask="<<fuzzy<<" spelling="<<spelling<<" candidates="<<draft.candidates().size();
                    if(!draft.candidates().empty())std::cout<<" first="<<to_utf8(draft.candidates()[0].text);std::cout<<'\n';
                    if(spelling=="wan")require(!draft.candidates().empty()&&draft.candidates()[0].text.size()==1,"wan full-pinyin priority regression");
                }
                std::sort(timings.begin(),timings.end());
                std::cout<<"PERF mask="<<fuzzy<<" keys="<<timings.size()<<" median_ms="<<timings[timings.size()/2]<<" p95_ms="<<timings[timings.size()*95/100]<<" max_ms="<<timings.back()<<'\n';
            }
            return 0;
        }catch(const std::exception& e){std::cerr<<"PERF failed: "<<e.what()<<'\n';return 1;}
    }
    unsigned passed=0,failed=0;
    auto test=[&](const char* name,const std::function<void()>& body) {
        try { body(); ++passed; std::cout<<"PASS "<<name<<'\n'; }
        catch(const std::exception& e) { ++failed; std::cout<<"FAIL "<<name<<": "<<e.what()<<'\n'; }
    };
    test("whole_sentence_decoding",[] {
        auto d=dictionary(); auto c=d->suggest("nihaowodaolejia");
        require(!c.empty() && c[0].text==L"你好我到了家" && c[0].consumed==15,"full sentence not decoded");
    });
    test("explicit_syllable_boundary",[] {
        auto d=dictionary(); auto c=d->suggest("xi'an");
        require(!c.empty() && c[0].text==L"西安","apostrophe ignored");
        for(const auto& v:c) require(v.text!=L"先","single syllable crossed explicit boundary");
    });
    test("umlaut_and_partial_pinyin",[] {
        auto d=dictionary(); require(d->suggest("nv")[0].text==L"女","nv failed");
        require(d->suggest("lv")[0].text==L"绿","lv failed");
        auto c=d->suggest("niha"); require(!c.empty() && c[0].text==L"你好","partial final syllable failed");
    });
    test("utf8_rejects_invalid_and_roundtrips",[] {
        const std::string text="你好𠀀";
        require(to_utf8(from_utf8(text))==text,"Unicode roundtrip failed");
        bool rejected=false; try { from_utf8("\xc0\x80"); } catch(const std::invalid_argument&) { rejected=true; }
        require(rejected,"overlong UTF-8 accepted");
    });
    test("selection_space_not_trigger_space",[] {
        DraftSession s(dictionary()); type(s,"nihao"); choose(s);
        require(s.draft()==L"你好" && s.pinyin().empty(),"selection did not preserve owned draft");
        auto commit=confirm(s); require(commit.text==L"你好" && commit.learn,"bad learning commit");
        require(s.draft()==L"你好","draft cleared before acknowledgement");
    });
    test("long_space_gaps_and_key_repeat",[] {
        DraftSession s(dictionary()); type(s,"nihao"); choose(s);
        require(!s.key({KeyKind::Space,0,100}).commit,"single space committed");
        require(!s.key({KeyKind::Space,0,900}).commit,"slow spaces counted");
        require(!s.key({KeyKind::Space,0,1000,true}).commit,"held space committed");
        confirm(s,1200);
    });
    test("enter_submits_without_learning_or_sending",[] {
        DraftSession s(dictionary()); type(s,"nihao");choose(s);
        auto r=s.key({KeyKind::Enter}); require(r.consumed && r.commit && !r.commit->learn,"Enter wrong semantics");
        require(r.commit->text==L"你好","Enter changed confirmed Chinese");
    });
    test("written_ack_exactly_once",[] {
        DraftSession s(dictionary()); type(s,"nihao"); choose(s); auto c=confirm(s);
        require(!s.acknowledge(c.id+1,CommitResult::Written),"wrong ack accepted");
        require(!s.empty(),"wrong ack lost draft");
        require(s.acknowledge(c.id,CommitResult::Written) && s.empty(),"written ack failed");
        require(!s.acknowledge(c.id,CommitResult::Written),"duplicate ack accepted");
        require(!s.key({KeyKind::Space,0,500}).consumed,"empty state swallowed spaces");
    });
    test("failed_write_keeps_retryable_draft",[] {
        DraftSession s(dictionary()); type(s,"nihao"); choose(s); auto first=confirm(s);
        require(s.acknowledge(first.id,CommitResult::NotWritten),"failure ack rejected");
        require(s.draft()==L"你好" && !s.commit_pending(),"failed write lost/stuck draft");
        auto second=confirm(s,500); require(second.id!=first.id && second.text==first.text,"explicit retry invalid");
    });
    test("uncertain_write_does_not_freeze_next_sentence",[] {
        DraftSession s(dictionary()); type(s,"nihao"); choose(s); auto c=confirm(s);
        s.acknowledge(c.id,CommitResult::Unknown);
        require(s.uncertain() && s.recovery_draft()==L"你好" && !s.commit_pending(),"recovery snapshot missing");
        type(s,"wo"); choose(s); auto next=confirm(s,500);
        require(next.text==L"我","uncertain sentence replayed or new input frozen");
    });
    test("drafts_are_context_local",[] {
        auto d=dictionary(); DraftSession a(d),b(d); type(a,"nihao"); choose(a); type(b,"wo"); choose(b);
        require(confirm(a).text==L"你好" && confirm(b).text==L"我","cross-context draft leak");
    });
    test("edit_invalidates_space_gesture",[] {
        DraftSession s(dictionary()); type(s,"nihao"); choose(s); s.key({KeyKind::Space,0,100});
        s.key({KeyKind::Backspace}); type(s,"hao"); choose(s); auto c=confirm(s,300);
        require(c.text==L"你好","editing/gesture reset failed");
    });
    test("surrogate_character_deletion",[] {
        DraftSession s(dictionary()); type(s,"he"); choose(s);
        require(to_utf8(s.draft())=="𠀀","supplementary character selection failed");
        s.key({KeyKind::Backspace}); require(s.empty(),"backspace split surrogate pair");
    });
    test("candidate_digit_and_punctuation",[] {
        DraftSession s(dictionary()); type(s,"xian");
        require(s.candidates().size()>=2,"homophone candidates missing");
        const auto expected=s.candidates()[1].text;
        s.key({KeyKind::Digit,L'2'}); s.key({KeyKind::Punctuation,L'。'});
        require(confirm(s).text==expected+L"。","candidate or punctuation failed");
    });
    test("missing_dictionary_passes_input",[] {
        DraftSession s(std::make_shared<Dictionary>());
        require(!s.key({KeyKind::Letter,L'n'}).consumed,"unavailable dictionary swallowed input");
    });
    test("bad_import_cannot_destroy_dictionary",[] {
        auto d=dictionary(); auto count=d->size();
        Fixture bad("ni\t你\tnan\nxx\t坏\t-1\n"); bool rejected=false;
        try { d->load(bad.path); } catch(const std::runtime_error&) { rejected=true; }
        require(rejected && d->size()==count && d->suggest("nihao")[0].text==L"你好","invalid import destroyed data");
    });
    test("user_dictionary_overlay_and_duplicate_handling",[] {
        auto d=dictionary(); auto count=d->size();
        Fixture user("mansuer\t曼苏尔\t9000\nnihao\t你好\t900\nni\t你\tbad\n");
        auto report=d->load(user.path);
        require(report.loaded==2 && report.rejected==1 && d->size()==count+1,"import accounting incorrect");
        require(d->suggest("mansuer")[0].text==L"曼苏尔","imported term unavailable");
    });
    test("incomplete_pinyin_can_be_corrected",[] {
        DraftSession s(dictionary()); type(s,"nihaoxx");
        s.key({KeyKind::Backspace}); s.key({KeyKind::Backspace}); choose(s);
        require(confirm(s).text==L"你好","typo correction failed");
    });
    test("model_unavailable_is_not_an_input_dependency",[] {
        DraftSession s(dictionary());
        for(unsigned i=0;i<20;++i) {
            type(s,"nihao"); choose(s); auto c=confirm(s,1000*i+100);
            s.acknowledge(c.id,CommitResult::Written);
            // No translator or event consumer exists: subsequent input must still work.
        }
        require(s.empty() && !s.commit_pending(),"input depends on learning acknowledgement");
    });
    test("binary_dictionary_roundtrip",[] {
        auto d=dictionary(); Fixture binary("",".mlex"); d->save_binary(binary.path);
        Dictionary restored; restored.load(binary.path);
        require(restored.size()==d->size(),"binary entry count changed");
        require(restored.suggest("xi'an")[0].text==L"西安","binary lost syllable boundaries");
        require(restored.suggest("nihaowodaolejia")[0].text==L"你好我到了家","binary changed sentence decoding");
    });
    test("corrupt_binary_keeps_previous_dictionary",[] {
        auto d=dictionary(); const auto count=d->size(); Fixture binary("",".mlex"); d->save_binary(binary.path);
        { std::fstream file(binary.path,std::ios::in|std::ios::out|std::ios::binary);
          file.seekg(30); char byte=0; file.get(byte); file.seekp(30); file.put(static_cast<char>(byte^1)); }
        bool rejected=false; try { d->load(binary.path); } catch(const std::runtime_error&) { rejected=true; }
        require(rejected && d->size()==count && d->suggest("nihao")[0].text==L"你好","corrupt cache replaced working dictionary");
    });
    test("low_frequency_homophones_remain_reachable",[] {
        std::string contents;
        for(unsigned i=0;i<50;++i) {
            const std::wstring word(1,static_cast<wchar_t>(0x4e00+i));
            contents+="shi\t"+to_utf8(word)+"\t"+std::to_string(100-i)+"\n";
        }
        Fixture fixture(contents); Dictionary d; d.load(fixture.path);
        const auto candidates=d.suggest("shi",512);
        require(candidates.size()==50,"rare homophones dropped by sentence beam pruning");
    });
    test("allocation_failure_cannot_strand_commit_or_consume_draft",[] {
        DraftSession original(dictionary()); type(original,"nihao"); choose(original);
        for(unsigned i=0;i<24;++i) original.key({KeyKind::Punctuation,L'。'});
        type(original,"nihao");
        for(const Key action : {Key{KeyKind::Enter},Key{KeyKind::Space,0,100},Key{KeyKind::Letter,L'w'},Key{KeyKind::Literal,L'!'},Key{KeyKind::EnglishSpace,0,100}}) {
            bool reached_success=false; unsigned failures=0;
            for(long budget=0;budget<4096;++budget) {
                auto candidate=original; bool failed=false;
                allocation_fault::remaining=budget;
                try { candidate.key(action); }
                catch(const std::bad_alloc&) { failed=true; }
                catch(...) { allocation_fault::remaining=-1; throw; }
                allocation_fault::remaining=-1;
                if(!failed) { reached_success=true; break; }
                ++failures;
                require(!candidate.commit_pending() && candidate.pinyin()==original.pinyin() &&
                        candidate.draft()==original.draft() && candidate.caret()==original.caret() &&
                        candidate.revision()==original.revision() && candidate.selected()==original.selected(),
                        "allocation failure partially published key state");
                require(candidate.accept_raw(),"failed allocation prevented raw confirmation");
                const auto retry=candidate.key({KeyKind::Enter});
                require(retry.commit && candidate.acknowledge(retry.commit->id,CommitResult::Written) && candidate.empty(),
                        "failed allocation prevented a subsequent explicit commit");
            }
            require(reached_success && failures>0,"fault injection did not cover all allocation sites");
        }
    });
    test("abbreviation_mixed_and_explicit_boundaries",[] {
        auto d=dictionary();
        for(const auto input:{"nh","nhao","nih","n'hao"})
            require(includes(d->suggest(input,512),L"你好"),"initial or mixed spelling missing");
        require(!includes(d->suggest("nh",512,{false,0}),L"你好"),"disabled abbreviation still active");
        require(!includes(d->suggest("xi'an",512),L"先"),"abbreviation crossed apostrophe");
        Fixture interjections("n\t嗯\t900000\tn\nhe\t和\t800000\the\n");d->load(interjections.path);
        require(d->suggest("nh",512)[0].text==L"你好"&&d->suggest("nhao",512)[0].text==L"你好","standalone interjection displaced normal abbreviated phrase");
    });
    test("full_pinyin_precedes_abbreviation_without_word_special_cases",[] {
        Fixture source("wan\t玩\t100\twan\nwan\t晚\t20\twan\nwoaini\t我爱你\t999999\two ai ni\n");
        Dictionary d;d.load(source.path);
        const auto values=d.suggest("wan",512);
        require(values.size()>=3&&values[0].text==L"玩"&&values[1].text==L"晚","abbreviation displaced exact full pinyin");
        require(includes(values,L"我爱你"),"valid abbreviation should remain selectable after exact candidates");
        require(includes(d.suggest("w'ai'n",512),L"我爱你"),"explicit abbreviated syllables failed");
    });
    test("known_whole_word_precedes_more_frequent_composed_guess",[] {
        Fixture source("zhi\t之\t100000000\tzhi\nteng\t疼\t100000000\tteng\n"
                       "yao\t要\t100000000\tyao\nzhitengyao\t止疼药\t1\tzhi teng yao\n");
        Dictionary d;d.load(source.path);
        for(const auto spelling:{"zhitengyao","zhi'teng'yao"}) {
            const auto values=d.suggest(spelling,512,{false,0});
            require(!values.empty()&&values[0].text==L"止疼药","complete dictionary word lost to frequency-composed guess");
            require(includes(values,L"之疼要"),"composed alternatives should remain selectable");
            require(d.suggest(spelling,1,{false,0})[0].text==L"止疼药","candidate limit changed exact priority");
        }
        require(!includes(d.suggest("zh'itengyao",512,{false,0}),L"止疼药"),"exact-word priority ignored explicit syllable boundaries");
    });
    test("known_whole_word_survives_prefix_beam_and_preserves_homophone_order",[] {
        std::string rows="zhi\t止\t1\tzhi\ntong\t痛\t100000000\ttong\n"
                         "yao\t要\t100000000\tyao\nzhitongyao\t止痛药\t1\tzhi tong yao\n";
        for(unsigned i=0;i<40;++i)
            rows+="zhi\t"+to_utf8(std::wstring(1,static_cast<wchar_t>(0x4e00+i)))+"\t100000000\tzhi\n";
        Fixture source(rows);Dictionary d;d.load(source.path);
        require(d.suggest("zhitongyao",9,{false,0})[0].text==L"止痛药","whole word erased by bounded prefix beam");
        auto original=dictionary();const auto exact=original->suggest("xian",512,{false,0});
        require(exact.size()>=2&&exact[0].text==L"先"&&exact[1].text==L"西安","single and multisyllable exact frequency order changed");
    });
    test("query_limits_still_bound_full_and_fuzzy_input",[] {
        auto d=dictionary();
        require(d->suggest(std::string(129,'n'),512,{true,255}).empty(),"overlength fuzzy query accepted");
        require(d->suggest("nihao",0,{true,255}).empty(),"zero candidate limit ignored");
        require(d->suggest("nihao",10000,{true,255}).size()<=512,"candidate limit not bounded");
        require(d->suggest("'nihao",512,{true,255}).empty(),"invalid leading apostrophe accepted");
    });
    test("each_fuzzy_pair_is_opt_in_and_combinations_work",[] {
        struct Case {const char* canonical;const char* typed;unsigned mask;};
        for(const auto& c:std::vector<Case>{{"zha","za",1},{"cha","ca",2},{"sha","sa",4},{"na","la",8},
                {"fa","ha",16},{"an","ang",32},{"en","eng",64},{"lin","ling",128}}) {
            Fixture source(std::string(c.canonical)+"\t字\t100\t"+c.canonical+"\n");
            Dictionary d;d.load(source.path);
            const auto off=d.suggest(c.typed,512,{false,0});const auto on=d.suggest(c.typed,512,{false,c.mask});
            auto full=[&](const Candidate& value){return value.text==L"字"&&value.consumed==std::string_view(c.typed).size();};
            require(std::none_of(off.begin(),off.end(),full),"default fuzzy must be off");
            require(std::any_of(on.begin(),on.end(),full),"enabled fuzzy pair unavailable");
        }
        Fixture source("zhang\t张\t100\tzhang\nzan\t赞\t1\tzan\n");Dictionary d;d.load(source.path);
        require(!includes(d.suggest("zan",512,{false,1}),L"张"),"disabled final fuzzy pair leaked");
        const auto combined=d.suggest("zan",512,{false,33});
        require(combined[0].text==L"赞"&&includes(combined,L"张"),"combined fuzzy lost exact priority");
    });
    test("syllable_inference_for_three_column_user_words",[] {
        Fixture source("nihao\t你好\t100\nxian\t西安\t50\nzhongguo\t中国\t90\n");
        Dictionary d;d.load(source.path);
        require(includes(d.suggest("nh",512),L"你好"),"three-column word lacks abbreviation");
        require(includes(d.suggest("xi'an",512),L"西安"),"character-count syllable inference failed");
        require(includes(d.suggest("zhg",512),L"中国"),"two-letter initial abbreviation failed");
        Fixture binary("",".mlex");d.save_binary(binary.path);Dictionary restored;restored.load(binary.path);
        require(includes(restored.suggest("zg",512),L"中国"),"binary reload lost inferred syllables");
    });
    test("page_keys_use_stable_nine_candidate_pages",[] {
        std::string contents;
        for(unsigned i=0;i<23;++i)contents+="shi\t"+to_utf8(std::wstring(1,static_cast<wchar_t>(0x4e00+i)))+"\t"+std::to_string(100-i)+"\n";
        Fixture source(contents);auto d=std::make_shared<Dictionary>();d->load(source.path);DraftSession s(d);
        require(!s.wants({KeyKind::PageDown}),"empty input consumes page key");
        type(s,"shi");s.set_options({false,0});
        s.key({KeyKind::PageUp});require(s.selected()==0,"first page wrapped");
        s.key({KeyKind::PageDown});require(s.selected()==9,"page down did not choose next page start");
        const auto expected=s.candidates()[17].text;
        s.key({KeyKind::Digit,L'9'});require(s.draft()==expected,"digit selected wrong page");
        type(s,"shi");s.key({KeyKind::PageDown});s.key({KeyKind::PageDown});
        require(s.selected()==18,"short last page index invalid");
        s.key({KeyKind::PageDown});require(s.selected()==18,"last page wrapped");
        const auto count=s.draft().size();s.key({KeyKind::Digit,L'9'});require(s.draft().size()==count,"invalid last-page digit selected another candidate");
        s.key({KeyKind::PageUp});require(s.selected()==9,"page up not stable");
    });
    test("option_refresh_is_transactional_and_noop_is_free",[] {
        DraftSession original(dictionary());type(original,"nihao");choose(original);type(original,"nh");
        const auto revision=original.revision();original.set_options(original.options());
        require(original.revision()==revision,"unchanged options changed draft revision");
        bool success=false;unsigned faults=0;
        for(long budget=0;budget<4096;++budget) {
            auto s=original;allocation_fault::remaining=budget;bool failed=false;
            try{s.set_options({false,255});}catch(const std::bad_alloc&){failed=true;}
            catch(...){allocation_fault::remaining=-1;throw;}
            allocation_fault::remaining=-1;
            if(!failed){require(s.revision()==revision+1&&s.draft()==original.draft()&&!s.options().abbreviation,"option refresh damaged selected draft");success=true;break;}
            ++faults;require(s.options().abbreviation&&s.options().fuzzy_mask==0&&s.pinyin()==original.pinyin()&&s.revision()==revision,"failed option refresh partially published");
        }
        require(success&&faults,"option change allocation coverage incomplete");
    });
    test("strict_four_column_rows_and_control_characters",[] {
        Fixture source("nihao\t你好\t100\tni hao\nnihao\t坏词\t20\tnih ao\nhao\t好\t 20\thao\nni\t你\xC2\x85\t10\tni\n");
        Dictionary d;const auto report=d.load(source.path);
        require(report.loaded==1&&report.rejected==3&&report.first_rejected_line==2,"strict row validation or line number failed");
    });
    test("sustained_context_gestures_and_commit_recovery_do_not_disable_typing",[] {
        auto d=dictionary();DraftSession a(d),b(d);
        for(unsigned i=0;i<100;++i) {
            auto& active=i%2?a:b;
            active.set_options({true,i%3?0u:255u});
            type(active,i%2?"nh":"nihao");choose(active);
            active.key({KeyKind::Space,0,1000*i+10});active.reset_gesture();
            require(!active.key({KeyKind::Space,0,1000*i+100}).commit,"focus reset kept the old partial gesture");
            active.reset_gesture();auto commit=confirm(active,1000*i+300);
            require(!active.key({KeyKind::Letter,L'n'}).consumed,"pending write accepted extra input");
            active.set_options({true,i%2?255u:0u});
            require(active.acknowledge(commit.id,CommitResult::NotWritten),"matching failure ack not accepted");
            const auto retry=active.key({KeyKind::Enter});require(retry.commit.has_value(),"explicit retry remained blocked");
            require(active.acknowledge(retry.commit->id,i%7?CommitResult::Written:CommitResult::Unknown),"retry ack failed");
            require(active.empty()&&!active.commit_pending(),"a settled session disabled following input");
        }
    });
    test("candidate_reading_comes_from_source_for_every_match_route",[] {
        const auto d=dictionary();
        for(const auto spelling:{"nihao","nh","nhao","nih","niha"}) {
            const auto values=d->suggest(spelling,512);
            const auto found=std::find_if(values.begin(),values.end(),[](const auto& value){return value.text==L"你好";});
            require(found!=values.end()&&found->syllables=="ni hao","abbreviation or completion stored raw keys as reading");
        }
        const auto sentence=d->suggest("nihaowodaolejia");
        require(sentence[0].syllables=="ni hao wo dao le jia","composed path lost source syllables");
        Fixture fuzzy("nai\t奶\t100\tnai\n");Dictionary f;f.load(fuzzy.path);
        const auto values=f.suggest("lai",512,{false,8});
        require(!values.empty()&&values[0].text==L"奶"&&values[0].syllables=="nai","fuzzy spelling contaminated canonical reading");
    });
    test("personal_words_are_strict_bounded_and_duplicates_do_not_double_count",[] {
        require(valid_personal_word({L"九班","jiu ban",1}),"valid personal phrase rejected");
        require(valid_personal_word({from_utf8("𠀀"),"he",1}),"supplementary Han not counted as one character");
        for(const auto& word:std::vector<PersonalWord>{{L"九班","jiub",1},{L"九班","jiu",1},{L"九班","jiu  ban",1},
                {L"九班","jiu ban ",1},{L"九班","Jiu ban",1},{L"九班","jiu ban",0},{L"九班","jiu ban",1000001},
                {L"九A","jiu a",1},{L"九。","jiu a",1},{L"一二三四五六七八九","yi er san si wu liu qi ba jiu",1}})
            require(!valid_personal_word(word),"unsafe or noncanonical personal word accepted");
        PersonalLexicon merged({{L"九班","jiu ban",2},{L"九班","jiu ban",3}});
        require(merged.words().size()==1&&merged.words()[0].uses==3,"duplicate snapshot rows counted twice");
        bool rejected=false;try{PersonalLexicon oversized(std::vector<PersonalWord>(10001,{L"九班","jiu ban",1}));}
        catch(const std::invalid_argument&){rejected=true;}require(rejected,"personal index limit ignored");
    });
    test("completion_finishes_last_syllable_without_predicting_extra_words",[] {
        Fixture source("jintian\t今天\t100\tjin tian\n"
                       "jintianbuwan\t今天不玩\t1000000\tjin tian bu wan\n"
                       "jintianbuwanmoshoushijie\t今天不玩魔兽世界\t1000000\tjin tian bu wan mo shou shi jie\n");
        Dictionary d;d.load(source.path);
        for(const auto query:{"jint","jintian","jt"}) {
            const auto values=d.suggest(query,512);
            require(includes(values,L"今天"),"normal final-syllable or initials match lost");
            require(!includes(values,L"今天不玩")&&!includes(values,L"今天不玩魔兽世界"),"prefix expanded beyond typed syllables");
        }
        require(includes(d.suggest("jintianbuwanmoshoushijie",512),L"今天不玩魔兽世界"),"complete explicit input lost long entry");
        require(!includes(d.suggest("jint'",512,{false,0}),L"今天"),"declared complete syllable was silently finished");
    });
    test("completion_cache_filters_before_frequency_pruning_and_binary_roundtrip",[] {
        std::string rows="jintian\t今天\t1\tjin tian\n";
        for(unsigned i=0;i<30;++i) {
            rows+="jintiandehao\t"+to_utf8(L"今天的"+std::wstring(1,static_cast<wchar_t>(0x4e10+i)))+"\t1000000\tjin tian de hao\n";
        }
        Fixture source(rows);Dictionary d;d.load(source.path);
        require(includes(d.suggest("jint",512,{false,0}),L"今天"),"long frequent entries evicted matching word from bounded prefix cache");
        Fixture binary("", ".mlex");d.save_binary(binary.path);Dictionary reopened;reopened.load(binary.path);
        const auto values=reopened.suggest("jint",512,{false,0});
        require(includes(values,L"今天"),"binary dictionary lost filtered completion cache");
        for(const auto& value:values)require(value.text.size()<=2,"binary dictionary predicted untyped syllables");
    });
    test("saved_long_phrase_frequency_cannot_expand_short_pinyin",[] {
        Fixture source("jintian\t今天\t100\tjin tian\n");auto d=std::make_shared<Dictionary>();d->load(source.path);
        auto personal=std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{{L"今天不玩魔兽世界","jin tian bu wan mo shou shi jie",1000000}});
        for(const auto query:{"jint","jintian","jt"}) {
            DraftSession s(d);s.set_personal_lexicon(personal);type(s,query);
            require(candidate_position(s,L"今天")<9,"normal word lost to personal history");
            require(!includes(s.candidates(),L"今天不玩魔兽世界"),"frequent saved long phrase ignored completion boundary");
        }
        DraftSession exact(d);exact.set_personal_lexicon(personal);type(exact,"jintianbuwanmoshoushijie");
        require(exact_candidate(exact,L"今天不玩魔兽世界"),"stored long entry was removed instead of matched accurately");
    });
    test("personal_completion_does_not_force_rare_homophone_above_base_frequency",[] {
        Fixture source("qingting\t倾听\t10000\tqing ting\nqingting\t清廷\t1\tqing ting\n");
        auto d=std::make_shared<Dictionary>();d->load(source.path);
        auto personal=std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{{L"清廷","qing ting",1000000}});
        DraftSession partial(d);partial.set_personal_lexicon(personal);type(partial,"qingti");
        require(partial.candidates()[0].text==L"倾听","incomplete spelling force-promoted rare remembered alternative");
        DraftSession exact(d);exact.set_personal_lexicon(personal);type(exact,"qingting");
        require(exact.candidates()[0].text==L"清廷","accurate complete reading lost intended personal preference");
    });
    test("automatic_compounds_stop_at_four_characters_but_explicit_words_remain",[] {
        Fixture source("jintian\t今天\t100\tjin tian\nbuwan\t不玩\t100\tbu wan\n"
                       "moshoushijie\t魔兽世界\t100\tmo shou shi jie\n"
                       "jintianbuwanmoshoushijie\t今天不玩魔兽世界\t100\tjin tian bu wan mo shou shi jie\n");
        auto d=std::make_shared<Dictionary>();d->load(source.path);DraftSession s(d);
        type(s,"jintian");select_word(s,L"今天");type(s,"buwan");select_word(s,L"不玩");
        type(s,"moshoushijie");select_word(s,L"魔兽世界");const auto commit=confirm(s);
        require(commit.text==L"今天不玩魔兽世界","learning restriction changed normal draft");
        require(observations(commit,L"今天不玩","jin tian bu wan")==1,"short compound formation was lost");
        require(observations(commit,L"今天不玩魔兽世界","jin tian bu wan mo shou shi jie")==0,"adjacent words were learned as a whole sentence");
        for(const auto& word:commit.personal_words)require(word.text.size()<=4,"automatic join saved long sentence fragment");
        DraftSession explicit_word(d);type(explicit_word,"jintianbuwanmoshoushijie");select_word(explicit_word,L"今天不玩魔兽世界");
        require(observations(confirm(explicit_word),L"今天不玩魔兽世界","jin tian bu wan mo shou shi jie")==1,"explicit long dictionary selection was discarded");
    });
    test("separately_selected_word_is_immediately_available_and_repetition_promotes_it",[] {
        DraftSession s(personal_fixture());type(s,"jiu");select_word(s,L"九");type(s,"ban");select_word(s,L"班");
        type(s,"jiuban");const auto first=candidate_position(s,L"九班");
        require(first<9&&exact_candidate(s,L"九班"),"newly composed word absent as an exact entry on first page in same draft");
        select_word(s,L"九班");type(s,"jiuban");
        require(candidate_position(s,L"九班")==0,"second observation did not promote remembered word");
        select_word(s,L"九班");type(s,"jiuban");
        require(candidate_position(s,L"九班")==0,"third observation lost promotion");
        select_word(s,L"九班");const auto commit=confirm(s);
        require(observations(commit,L"九班","jiu ban")==4,"one selected span or combination counted more than once");
    });
    test("written_commit_delta_can_be_reloaded_without_replaying_draft_counts",[] {
        auto d=personal_fixture();DraftSession s(d);
        type(s,"jiu");select_word(s,L"九");type(s,"ban");select_word(s,L"班");auto commit=confirm(s);
        require(observations(commit,L"九班","jiu ban")==1,"formed phrase not included in commit delta");
        require(s.acknowledge(commit.id,CommitResult::Written),"written acknowledgement failed");
        auto saved=std::make_shared<PersonalLexicon>(commit.personal_words);s.set_personal_lexicon(saved);
        type(s,"jiuban");select_word(s,L"九班");auto next=confirm(s,500);
        require(observations(next,L"九班","jiu ban")==1,"persisted count was emitted again as new usage");
        DraftSession restarted(d);restarted.set_personal_lexicon(saved);type(restarted,"jiuban");
        require(candidate_position(restarted,L"九班")<9,"remembered word unavailable in new session");
    });
    test("failed_or_unknown_commits_do_not_publish_or_duplicate_learning",[] {
        DraftSession s(personal_fixture());type(s,"jiu");select_word(s,L"九");type(s,"ban");select_word(s,L"班");
        auto first=confirm(s);require(s.acknowledge(first.id,CommitResult::NotWritten),"failed write not acknowledged");
        auto retry=confirm(s,500);
        require(observations(first,L"九班","jiu ban")==1&&observations(retry,L"九班","jiu ban")==1,"retry counted choice twice");
        require(s.acknowledge(retry.id,CommitResult::Unknown),"unknown write not acknowledged");
        type(s,"jiuban");require(!exact_candidate(s,L"九班"),"unknown result retained provisional word");
    });
    test("deletion_escape_and_mid_draft_edits_remove_provisional_observations",[] {
        for(const auto action:{KeyKind::Backspace,KeyKind::Escape}) {
            DraftSession s(personal_fixture());type(s,"jiu");select_word(s,L"九");type(s,"ban");select_word(s,L"班");
            s.key({action});type(s,"jiuban");require(!exact_candidate(s,L"九班"),"deleted/cancelled phrase stayed personalized");
        }
        DraftSession s(personal_fixture());type(s,"jiu");select_word(s,L"九");type(s,"ban");select_word(s,L"班");
        s.key({KeyKind::Left});type(s,"jiu");select_word(s,L"九");
        const auto commit=s.key({KeyKind::Enter}).commit;
        require(commit&&observations(*commit,L"九班","jiu ban")==0,"mid-draft insertion retained stale phrase observation");
    });
    test("punctuation_digit_cursor_and_cancel_break_phrase_assembly",[] {
        for(const Key boundary:{Key{KeyKind::Punctuation,L'。'},Key{KeyKind::Digit,L'1'},Key{KeyKind::Left},Key{KeyKind::Escape}}) {
            DraftSession s(personal_fixture());type(s,"jiu");select_word(s,L"九");s.key(boundary);
            if(boundary.kind==KeyKind::Left)s.key({KeyKind::Right});
            type(s,"ban");select_word(s,L"班");const auto commit=s.key({KeyKind::Enter}).commit;
            require(commit&&observations(*commit,L"九班","jiu ban")==0,"phrase crossed a non-word boundary");
        }
    });
    test("personal_overlay_supports_full_initial_mixed_fuzzy_and_prefix_readings",[] {
        const auto d=personal_fixture();auto personal=std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{{L"九班","jiu ban",3},{L"我爱你","wo ai ni",1000}});
        for(const auto spelling:{"jiuban","jb","jiub","jban","jiuba","jiu'ban"}) {
            DraftSession s(d);s.set_personal_lexicon(personal);type(s,spelling);
            const auto pos=candidate_position(s,L"九班");require(pos<s.candidates().size(),"remembered word missing in a standard spelling route");
            require(s.candidates()[pos].syllables=="jiu ban","personal candidate has noncanonical reading");
        }
        DraftSession normal(d);normal.set_personal_lexicon(personal);type(normal,"wan");
        require(normal.candidates()[0].text==L"玩","personal abbreviation displaced accurate full pinyin");
        auto fuzzy=std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{{L"奶班","nai ban",3}});
        DraftSession s(d);s.set_personal_lexicon(fuzzy);s.set_options({false,8});type(s,"laiban");
        const auto pos=candidate_position(s,L"奶班");require(pos<s.candidates().size()&&s.candidates()[pos].syllables=="nai ban","personal fuzzy route lost source reading");
    });
    test("single_character_frequency_is_remembered_and_disabled_mode_keeps_input",[] {
        DraftSession s(personal_fixture());
        for(unsigned i=0;i<2;++i){type(s,"jiu");select_word(s,L"九");s.key({KeyKind::Punctuation,L'。'});}
        type(s,"jiu");require(s.candidates()[0].text==L"九","single-character repetition did not affect ranking");
        s.set_personalization_enabled(false);require(s.candidates()[0].text==L"九","toggle changed the word already shown under a candidate number");
        select_word(s,L"九");auto commit=confirm(s);
        require(commit.personal_words.empty()&&!commit.text.empty(),"disabled memory discarded Chinese or emitted observations");
        require(s.acknowledge(commit.id,CommitResult::Written),"disabled normal input did not commit");
        type(s,"jiu");require(s.candidates()[0].text==L"久","disabled next conversion did not restore base");
        s.key({KeyKind::Escape});
        s.set_personal_lexicon(std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{{L"九","jiu",5}}));
        s.set_personalization_enabled(true);type(s,"jiu");require(s.candidates()[0].text==L"九","reenabling did not use retained saved snapshot");
    });
    test("async_snapshot_does_not_change_visible_selection_or_triple_space_gesture",[] {
        DraftSession s(personal_fixture());auto saved=std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{{L"九","jiu",20}});
        type(s,"jiu");const auto before=s.candidates()[1].text;const auto revision=s.revision();
        s.set_personal_lexicon(saved);
        require(s.revision()==revision&&s.candidates()[1].text==before,"async snapshot changed visible candidate number");
        s.key({KeyKind::Digit,L'2'});require(s.draft()==before,"number chose a word different from displayed candidate");
        s.key({KeyKind::Space,0,100});const auto stable=s.revision();s.set_personal_lexicon(saved);
        require(s.revision()==stable,"empty-pinyin snapshot unnecessarily changed revision");
        s.key({KeyKind::Space,0,200});auto third=s.key({KeyKind::Space,0,300});
        require(third.commit.has_value(),"snapshot interrupted triple-space confirmation");
        s.acknowledge(third.commit->id,CommitResult::Written);type(s,"jiu");require(s.candidates()[0].text==L"九","snapshot was not applied for next conversion");
    });
    test("snapshot_counts_and_current_draft_delta_are_added_exactly_once",[] {
        DraftSession s(personal_fixture());auto first=std::make_shared<PersonalLexicon>(std::vector<PersonalWord>{{L"九班","jiu ban",1}});
        s.set_personal_lexicon(first);type(s,"jiuban");select_word(s,L"九班");
        auto refreshed=std::make_shared<PersonalLexicon>(first->words());s.set_personal_lexicon(refreshed);
        type(s,"jiuban");require(s.candidates()[0].text==L"九班","saved+current uses did not promote word");
        select_word(s,L"九班");auto commit=confirm(s);
        require(observations(commit,L"九班","jiu ban")==2,"snapshot refresh duplicated already-observed usage");
    });
    test("personal_toggle_preserves_visible_revision_and_in_progress_confirmation",[] {
        DraftSession s(personal_fixture());type(s,"jiu");
        const auto revision=s.revision();const auto displayed=s.candidates()[1].text;
        s.set_personalization_enabled(false);
        require(s.revision()==revision&&s.candidates()[1].text==displayed,"memory toggle invalidated unchanged preview");
        s.key({KeyKind::Digit,L'2'});require(s.draft()==displayed,"toggle changed selection under the visible number");
        s.key({KeyKind::Space,0,100});s.set_personalization_enabled(true);
        s.key({KeyKind::Space,0,200});s.set_personalization_enabled(false);
        const auto third=s.key({KeyKind::Space,0,300});
        require(third.commit&&third.commit->personal_words.empty(),"memory toggle interrupted gesture or persisted disabled observations");
    });
    test("automatic_memory_is_bounded_per_draft_and_long_fragments_do_not_join",[] {
        std::string rows;
        for(unsigned i=0;i<80;++i)rows+="jiu\t"+to_utf8(std::wstring(1,static_cast<wchar_t>(0x4e10+i)))+"\t100\tjiu\n";
        Fixture source(rows);auto d=std::make_shared<Dictionary>();d->load(source.path);DraftSession s(d);
        for(unsigned i=0;i<80;++i) {
            type(s,"jiu");select_word(s,std::wstring(1,static_cast<wchar_t>(0x4e10+i)));
        }
        auto commit=confirm(s);require(commit.personal_words.size()==64,"draft memory cap was not exercised or exceeded");
        for(const auto& word:commit.personal_words)require(valid_personal_word(word)&&word.text.size()<=4,"auto memory produced invalid word or joined more than four pieces");
    });
    test("enter_confirms_original_letters_then_submits_on_second_press",[] {
        for(const auto value:{"mansur","github","Mansur","GitHub","Ni'Hao"}) {
            DraftSession s(dictionary());type(s,value);
            const auto raw=from_utf8(value);
            require(s.pinyin()==value&&s.preview()==raw,"original case/apostrophe lost while composing");
            const auto first=s.key({KeyKind::Enter});
            require(first.consumed&&first.changed&&!first.commit&&s.draft()==raw&&s.pinyin().empty(),"first Enter converted or submitted raw letters");
            const auto second=s.key({KeyKind::Enter});
            require(second.commit&&!second.commit->learn&&second.commit->text==raw&&second.commit->personal_words.empty(),"second Enter did not submit exact raw text");
        }
    });
    test("uppercase_query_keeps_chinese_choice_and_raw_consumption_offsets",[] {
        DraftSession s(dictionary());type(s,"NiHaO");
        require(s.candidates()[0].text==L"你好"&&s.candidates()[0].consumed==5,"case folding changed Chinese decoding");
        choose(s);require(s.draft()==L"你好","upper case Chinese choice failed");
        type(s,"Xi'An");require(s.candidates()[0].text==L"西安"&&s.candidates()[0].consumed==5,"case folding changed apostrophe offset");
        require(includes(s.candidates(),L"Xi'An"),"original apostrophe raw alternative missing");
        select_word(s,L"Xi'An");require(s.draft()==L"你好Xi'An"&&s.pinyin().empty(),"raw selection failed to consume original letters");
    });
    test("raw_candidate_preserves_chinese_order_and_page_reachability",[] {
        std::string rows;
        for(unsigned i=0;i<23;++i)rows+="shi\t"+to_utf8(std::wstring(1,static_cast<wchar_t>(0x4e10+i)))+"\t"+std::to_string(100-i)+"\tshi\n";
        Fixture source(rows);auto d=std::make_shared<Dictionary>();d->load(source.path);
        const auto original=d->suggest("shi",512);DraftSession s(d);type(s,"shi");
        require(s.candidates().size()==original.size()+1&&s.candidates()[8].kind==CandidateKind::Raw,"raw action not at ninth position");
        std::size_t i=0,raw_count=0;
        for(const auto& c:s.candidates()) {
            if(c.kind==CandidateKind::Raw) {++raw_count;require(c.syllables.empty()&&!c.whole_word,"raw candidate has personal syllables");}
            else require(c.text==original[i++].text,"Chinese candidate moved or disappeared");
        }
        require(i==23&&raw_count==1,"raw duplicated or original candidates lost");
        auto digit=s;digit.key({KeyKind::Digit,L'9'});require(digit.draft()==L"shi"&&digit.pinyin().empty(),"ninth digit did not select raw");
        auto selected=s;select_word(selected,L"shi");require(selected.draft()==digit.draft(),"selected raw differs from digit raw");
        s.key({KeyKind::PageDown});require(s.selected()==9&&s.candidates()[9].text==original[8].text,"original ninth Chinese candidate not reachable on next page");
        s.key({KeyKind::Digit,L'1'});require(s.draft()==original[8].text,"page number selected wrong Chinese candidate");
    });
    test("raw_candidate_exists_for_unknown_spelling_and_bounded_list",[] {
        DraftSession unknown(dictionary());type(unknown,"zzzzzz");
        require(unknown.candidates().size()==1&&unknown.candidates()[0].kind==CandidateKind::Raw,"unknown letters cannot be chosen");
        choose(unknown);require(unknown.draft()==L"zzzzzz","unknown raw selection changed text");
        std::string rows;
        for(unsigned i=0;i<530;++i)rows+="shi\t"+to_utf8(std::wstring(1,static_cast<wchar_t>(0x4e10+i)))+"\t"+std::to_string(1000-i)+"\tshi\n";
        Fixture source(rows);auto d=std::make_shared<Dictionary>();d->load(source.path);DraftSession s(d);type(s,"shi");
        require(s.candidates().size()==512&&s.candidates()[8].kind==CandidateKind::Raw,"raw insertion exceeded candidate cap");
        const auto raw_count=std::count_if(s.candidates().begin(),s.candidates().end(),[](const auto& c){return c.kind==CandidateKind::Raw;});
        require(raw_count==1,"raw candidate appeared across multiple pages");
    });
    test("mixed_text_remains_owned_in_order_and_english_is_not_personal_memory",[] {
        DraftSession s(dictionary());type(s,"nihao");choose(s);type(s,"GitHub");
        require(!s.key({KeyKind::Enter}).commit,"raw English escaped owned sentence");
        type(s,"wo");choose(s);type(s,"Mansur");select_word(s,L"Mansur");
        const auto commit=confirm(s);
        require(commit.learn&&commit.text==L"你好GitHub我Mansur","mixed confirmed sentence lost order or English");
        require(observations(commit,L"你好","ni hao")==1&&observations(commit,L"我","wo")==1,"English destroyed prior Han usage");
        require(observations(commit,L"你好我","ni hao wo")==0,"Chinese word observation crossed English span");
        for(const auto& word:commit.personal_words)require(valid_personal_word(word),"English leaked into personal words");
        s.acknowledge(commit.id,CommitResult::NotWritten);
        const auto retry=confirm(s,1000);require(retry.text==commit.text&&retry.personal_words.size()==commit.personal_words.size(),"failed write changed mixed sentence or memory");
    });
    test("literal_seals_raw_atomically_and_preserves_ascii_and_edit_order",[] {
        DraftSession s(dictionary());type(s,"nihao");choose(s);type(s,"GitHub");
        s.key({KeyKind::Literal,L'.'});literal(s,"com /Mansur_2");
        require(s.draft()==L"你好GitHub.com /Mansur_2"&&s.pinyin().empty(),"literal English punctuation was converted or reordered");
        s.key({KeyKind::Left});s.key({KeyKind::Backspace});s.key({KeyKind::Literal,L'-'});
        require(s.draft()==L"你好GitHub.com /Mansur-2","literal insertion did not follow owned caret");
        require(!s.key({KeyKind::Literal,L'\n'}).consumed&&!s.key({KeyKind::Literal,L'中'}).consumed,"nonprintable or nonASCII literal swallowed");
        const auto commit=s.key({KeyKind::Enter});require(commit.commit&&commit.commit->text==s.draft()&&!commit.commit->learn,"literal Enter sent or translated unexpectedly");
    });
    test("raw_edit_escape_and_unknown_write_keep_recovery_boundaries",[] {
        DraftSession s(dictionary());type(s,"GitHux");s.key({KeyKind::Backspace});type(s,"b");
        require(s.pinyin()=="GitHub","raw backspace lost original case");
        s.key({KeyKind::Escape});require(s.empty(),"Esc did not cancel pending raw");
        type(s,"nihao");choose(s);type(s,"GitHub");s.key({KeyKind::Escape});
        require(s.draft()==L"你好"&&s.pinyin().empty(),"raw cancellation removed previously owned Chinese");
        type(s,"Mansur");s.accept_raw();const auto commit=confirm(s);
        s.acknowledge(commit.id,CommitResult::Unknown);
        require(s.empty()&&s.recovery_draft()==L"你好Mansur","unknown mixed commit was not quarantined");
        literal(s,"Hello");require(confirm_english(s).text==L"Hello","unknown mixed sentence replayed into next English input");
    });
    test("accept_raw_empty_is_noop_and_allocation_failure_is_transactional",[] {
        DraftSession empty(dictionary());const auto revision=empty.revision();require(empty.accept_raw()&&empty.revision()==revision,"empty raw confirmation changed revision");
        literal(empty,"Hello");empty.key({KeyKind::EnglishSpace,0,100});empty.accept_raw();
        empty.key({KeyKind::EnglishSpace,0,200});require(empty.key({KeyKind::EnglishSpace,0,300}).commit.has_value(),"empty raw confirmation reset gesture");
        DraftSession original(dictionary());literal(original,"Before ");type(original,"GitHubMansur");
        bool success=false;unsigned failures=0;
        for(long budget=0;budget<4096;++budget) {
            auto next=original;bool failed=false;allocation_fault::remaining=budget;
            try {next.accept_raw();}catch(const std::bad_alloc&){failed=true;}catch(...){allocation_fault::remaining=-1;throw;}
            allocation_fault::remaining=-1;
            if(!failed){success=true;require(next.draft()==L"Before GitHubMansur"&&next.pinyin().empty(),"raw transaction success changed text");break;}
            ++failures;require(next.draft()==original.draft()&&next.pinyin()==original.pinyin()&&next.revision()==original.revision()&&next.caret()==original.caret(),"raw allocation failure published partial state");
        }
        require(success&&failures>0,"raw allocation failures untested");
    });
    test("raw_and_literal_capacity_failures_preserve_pending_letters",[] {
        DraftSession s(dictionary());literal(s,std::string(2047,'x'));type(s,"GH");const auto before=s.preview();const auto revision=s.revision();
        require(!s.accept_raw()&&s.preview()==before&&s.revision()==revision,"full draft raw confirmation partially modified text");
        for(const auto key:{Key{KeyKind::Literal,L'!'},Key{KeyKind::Enter},Key{KeyKind::EnglishSpace,0,100}}) {
            const auto result=s.key(key);require(result.consumed&&!result.changed&&!result.commit&&s.preview()==before&&s.revision()==revision,"capacity failure sealed or dropped raw text");
        }
        s.key({KeyKind::Backspace});require(s.accept_raw()&&s.draft().size()==2048&&s.draft().back()==L'G',"capacity recovery failed");
        const auto full=s.draft();require(!s.key({KeyKind::Literal,L'!'}).changed&&s.draft()==full,"full draft exceeded bound");
        DraftSession letters(dictionary());type(letters,std::string(128,'Z'));letters.key({KeyKind::Letter,L'Q'});
        require(letters.pinyin()==std::string(128,'Z')&&letters.accept_raw()&&letters.draft().size()==128,"raw limit or case preservation failed");
    });
    test("english_sentence_spaces_and_triple_confirmation_are_owned",[] {
        DraftSession s(dictionary());literal(s,"Hello");const auto space=s.key({KeyKind::EnglishSpace,0,100});
        require(space.changed&&!space.commit&&s.draft()==L"Hello ","ordinary English space lost");
        literal(s,"Mansur!");const auto commit=confirm_english(s,1000);
        require(commit.learn&&commit.text==L"Hello Mansur!"&&commit.personal_words.empty(),"English confirmation changed original text or learned Han word");
        require(s.acknowledge(commit.id,CommitResult::Written)&&s.empty(),"English Written did not clear exactly once");
    });
    test("english_gesture_preserves_previous_spaces_repeat_and_slow_input",[] {
        DraftSession s(dictionary());literal(s,"Hello  ");
        s.key({KeyKind::EnglishSpace,0,100});s.key({KeyKind::EnglishSpace,0,200});
        require(!s.key({KeyKind::EnglishSpace,0,300,true}).commit&&s.draft()==L"Hello     ","held space triggered or deleted ordinary spaces");
        const auto commit=confirm_english(s,500);require(commit.text==L"Hello     ","gesture removed older spaces");
        DraftSession slow(dictionary());literal(slow,"Hello");
        slow.key({KeyKind::EnglishSpace,0,100});slow.key({KeyKind::EnglishSpace,0,900});
        slow.key({KeyKind::EnglishSpace,0,800});
        require(slow.draft()==L"Hello   ","slow or backwards timestamp deleted ordinary spaces");
        const auto result=confirm_english(slow,2000);require(result.text==L"Hello   ","new English gesture retracted old spaces");
    });
    test("english_and_chinese_gestures_never_share_space_counts",[] {
        DraftSession s(dictionary());literal(s,"Hello");s.key({KeyKind::EnglishSpace,0,100});s.key({KeyKind::EnglishSpace,0,200});
        const auto chinese=confirm(s,300);require(chinese.text==L"Hello  ","Chinese gesture removed English spaces or mixed counts");
        DraftSession other(dictionary());literal(other,"Hello");other.key({KeyKind::Space,0,100});other.key({KeyKind::Space,0,200});
        const auto english=confirm_english(other,300);require(english.text==L"Hello","English gesture mixed Chinese counts");
        DraftSession mode(dictionary());literal(mode,"Hello");mode.key({KeyKind::EnglishSpace,0,100});mode.key({KeyKind::EnglishSpace,0,200});mode.reset_gesture();
        require(confirm_english(mode,300).text==L"Hello  ","explicit mode reset removed earlier ordinary spaces");
    });
    test("english_gesture_editing_retracts_only_current_caret_insertions",[] {
        DraftSession s(dictionary());literal(s,"Hello");s.key({KeyKind::EnglishSpace,0,100});s.key({KeyKind::EnglishSpace,0,200});
        s.key({KeyKind::Left});const auto commit=confirm_english(s,300);
        require(commit.text==L"Hello  ","middle-caret gesture removed preexisting trailing spaces");
        DraftSession edited(dictionary());literal(edited,"Hello");edited.key({KeyKind::EnglishSpace,0,100});edited.key({KeyKind::EnglishSpace,0,200});
        edited.key({KeyKind::Backspace});literal(edited,"GitHub");
        require(confirm_english(edited,400).text==L"Hello GitHub","edit reused old English gesture markers");
    });
    test("english_space_capacity_failure_never_erases_unrecorded_spaces",[] {
        DraftSession s(dictionary());literal(s,std::string(2047,'x'));s.key({KeyKind::EnglishSpace,0,100});
        require(s.draft().size()==2048&&!s.key({KeyKind::EnglishSpace,0,200}).changed,"full draft accepted second English space");
        const auto third=s.key({KeyKind::EnglishSpace,0,300});
        require(!third.commit&&!third.changed&&s.draft().size()==2048&&s.draft().back()==L' ',"failed space later retracted unrelated text");
        s.key({KeyKind::Backspace});s.key({KeyKind::Backspace});const auto commit=confirm_english(s,1000);
        require(commit.text==std::wstring(2046,L'x'),"third confirmation at full capacity failed");
    });
    test("english_failed_write_retry_and_unknown_do_not_retract_twice",[] {
        DraftSession s(dictionary());literal(s,"Hello  ");const auto first=confirm_english(s);
        require(first.text==L"Hello  "&&s.acknowledge(first.id,CommitResult::NotWritten),"English write failure lost plain spaces");
        require(s.draft()==first.text,"failed write retained shortcut spaces");
        const auto second=confirm_english(s,500);require(second.text==first.text&&second.id!=first.id,"retry removed spaces twice");
        s.acknowledge(second.id,CommitResult::Unknown);require(s.empty()&&s.recovery_draft()==first.text,"uncertain English snapshot wrong");
        literal(s,"Next");require(confirm_english(s,1000).text==L"Next","uncertain English snapshot was replayed");
    });
    test("english_empty_spaces_and_raw_prefix_have_consistent_confirmation",[] {
        DraftSession empty(dictionary());empty.key({KeyKind::EnglishSpace,0,100});empty.key({KeyKind::EnglishSpace,0,200});
        const auto third=empty.key({KeyKind::EnglishSpace,0,300});
        require(third.consumed&&third.changed&&!third.commit&&empty.empty(),"empty English shortcut passed through or produced empty commit");
        DraftSession raw(dictionary());type(raw,"Mansur");const auto commit=confirm_english(raw);
        require(commit.text==L"Mansur"&&raw.pinyin().empty()&&commit.personal_words.empty(),"English space did not confirm raw prefix exactly");
    });
    test("english_third_space_allocation_failure_preserves_text_and_gesture",[] {
        DraftSession original(dictionary());literal(original,"Hello Mansur, this stays owned.");
        original.key({KeyKind::EnglishSpace,0,100});original.key({KeyKind::EnglishSpace,0,200});
        bool success=false;unsigned failures=0;
        for(long budget=0;budget<4096;++budget) {
            auto next=original;bool failed=false;allocation_fault::remaining=budget;
            try {next.key({KeyKind::EnglishSpace,0,300});}catch(const std::bad_alloc&){failed=true;}catch(...){allocation_fault::remaining=-1;throw;}
            allocation_fault::remaining=-1;
            if(!failed){success=true;require(next.commit_pending(),"third English space did not prepare commit");break;}
            ++failures;
            require(next.draft()==original.draft()&&next.caret()==original.caret()&&next.revision()==original.revision()&&!next.commit_pending(),"third-space allocation failure removed owned spaces");
            const auto retry=next.key({KeyKind::EnglishSpace,0,300});
            require(retry.commit&&retry.commit->text==L"Hello Mansur, this stays owned.","failed third-space allocation lost gesture or duplicated text");
        }
        require(success&&failures>0,"third-space commit allocation boundary untested");
    });
    test("english_suggestions_are_optional_and_require_explicit_accept",[] {
        DraftSession raw(dictionary());literal(raw,"Hel");
        require(raw.english_completions().empty()&&!raw.wants({KeyKind::CompleteEnglish}),"default English input stole Tab");
        raw.set_english_suggestions(true);const auto suggestions=raw.english_completions();
        require(!suggestions.empty()&&suggestions[0]==L"Hello"&&raw.draft()==L"Hel","suggestions changed original spelling");
        auto accepted=raw.key({KeyKind::CompleteEnglish});require(accepted.consumed&&accepted.changed&&raw.draft()==L"Hello","Tab did not explicitly complete prefix");
        require(confirm_english(raw).personal_words.empty(),"English completion entered Chinese personal dictionary");
        DraftSession space(dictionary());space.set_english_suggestions(true);literal(space,"hel");space.key({KeyKind::EnglishSpace,0,100});
        require(space.draft()==L"hel ","ordinary space silently completed English word");
        DraftSession enter(dictionary());enter.set_english_suggestions(true);literal(enter,"hel");
        auto committed=enter.key({KeyKind::Enter});require(committed.commit&&committed.commit->text==L"hel","Enter silently changed English word");
    });
    test("english_completion_preserves_names_digits_case_and_identifier_boundaries",[] {
        for(const auto text:{"Mansur","GitHub","hello123","user@he","test_he","https://he","www.he"}) {
            DraftSession s(dictionary());s.set_english_suggestions(true);literal(s,text);
            require(s.english_completions().empty()&&!s.wants({KeyKind::CompleteEnglish}),"name or identifier offered unwanted correction");
            require(confirm_english(s).text==from_utf8(text),"English input changed literal token");
        }
        DraftSession upper(dictionary());upper.set_english_suggestions(true);literal(upper,"HEL");upper.key({KeyKind::CompleteEnglish});
        require(upper.draft()==L"HELLO","completion lost typed uppercase");
        DraftSession digit(dictionary());digit.set_english_suggestions(true);literal(digit,"he");digit.key({KeyKind::Literal,L'2'});
        require(digit.draft()==L"he2"&&digit.english_completions().empty(),"numeric English input selected candidate");
    });
    test("english_completion_declines_after_cancel_edit_and_mode_change",[] {
        DraftSession s(dictionary());s.set_english_suggestions(true);literal(s,"hello");s.key({KeyKind::Left});
        require(s.english_completions().empty(),"middle-of-word caret offered suffix replacement");
        s.key({KeyKind::Escape});literal(s,"he");s.set_english_suggestions(false);
        require(!s.key({KeyKind::CompleteEnglish}).consumed&&s.draft()==L"he","disabled suggestions consumed Tab");
        s.set_english_suggestions(true);s.key({KeyKind::Escape});
        require(s.english_completions().empty()&&!s.wants({KeyKind::CompleteEnglish}),"cancelled draft retained suggestions");
    });
    test("english_completion_allocation_failure_preserves_draft",[] {
        DraftSession original(dictionary());original.set_english_suggestions(true);literal(original,"This is a sufficiently long draft hel");bool success=false;unsigned failures=0;
        for(long budget=0;budget<1000;++budget){auto s=original;allocation_fault::remaining=budget;bool fault=false;
            try{s.key({KeyKind::CompleteEnglish});}catch(const std::bad_alloc&){fault=true;}catch(...){allocation_fault::remaining=-1;throw;}
            allocation_fault::remaining=-1;
            if(!fault){require(s.draft()==L"This is a sufficiently long draft hello","completion failed after allocations succeeded");success=true;break;}
            ++failures;require(s.draft()==original.draft()&&s.caret()==original.caret()&&s.revision()==original.revision(),"allocation failure changed visible prefix");
        }
        require(success&&failures>0,"completion allocation boundary not exercised");
    });
    std::cout<<"RESULT passed="<<passed<<" failed="<<failed<<'\n';
    return failed?1:0;
}
