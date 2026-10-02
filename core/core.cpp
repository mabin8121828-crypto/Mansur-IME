// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "mansur/core.hpp"
#include "english_completion.hpp"
#include "phonetic_index.hpp"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <type_traits>
#include <unordered_map>
#include <unordered_set>

namespace mansur {
namespace {
constexpr std::size_t beam_size=16;
constexpr std::size_t prefix_limit=24;
constexpr std::size_t max_pinyin=128;
constexpr std::size_t max_draft=2048;
constexpr std::size_t max_personal_words=10000;
constexpr std::size_t max_observations=64;
constexpr std::uint32_t max_personal_uses=1000000;
bool letter(char c) { return c>='a' && c<='z'; }
bool high(wchar_t c) { return c>=0xd800 && c<=0xdbff; }
bool low(wchar_t c) { return c>=0xdc00 && c<=0xdfff; }
bool control(wchar_t c) { return c<32 || (c>=127&&c<=159); }
std::size_t han_count(std::wstring_view text) noexcept {
    if(text.empty()||text.size()>16)return 0;
    std::size_t count=0;
    for(std::size_t i=0;i<text.size();++i) {
        std::uint32_t cp=static_cast<std::uint32_t>(text[i]);
        if(high(text[i])) {
            if(i+1>=text.size()||!low(text[i+1]))return 0;
            cp=0x10000+((cp-0xd800)<<10)+(static_cast<std::uint32_t>(text[++i])-0xdc00);
        } else if(low(text[i]))return 0;
        if(!(cp==0x3007||(cp>=0x3400&&cp<=0x4dbf)||(cp>=0x4e00&&cp<=0x9fff)||
             (cp>=0xf900&&cp<=0xfaff)||(cp>=0x20000&&cp<=0x323af)))return 0;
        if(++count>8)return 0;
    }
    return count;
}
std::size_t before(std::wstring_view s,std::size_t p) {
    if(p==0) return 0;
    --p;
    if(p>0 && low(s[p]) && high(s[p-1])) --p;
    return p;
}

bool valid_personal_word_impl(const PersonalWord& word) noexcept {
    try {
        const auto count=han_count(word.text);
        if(!count||!word.uses||word.uses>max_personal_uses||word.syllables.empty()||word.syllables.size()>55)return false;
        std::size_t start=0,syllables=0;
        while(start<word.syllables.size()) {
            const auto stop=word.syllables.find(' ',start);
            const auto part=std::string_view(word.syllables).substr(start,stop==std::string::npos?stop:stop-start);
            if(part.empty()||phonetic::syllable_id(part)<0||++syllables>count)return false;
            if(stop==std::string::npos)return syllables==count;
            start=stop+1;
        }
        return false; // A trailing separator is not canonical.
    }catch(...) { return false; }
}
std::size_t after(std::wstring_view s,std::size_t p) {
    if(p>=s.size()) return s.size();
    if(high(s[p]) && p+1<s.size() && low(s[p+1])) return p+2;
    return p+1;
}
std::vector<std::string_view> fields(std::string_view s) {
    std::vector<std::string_view> result;
    std::size_t start=0;
    while(true) {
        auto end=s.find('\t',start);
        result.push_back(s.substr(start,end==s.npos?s.size()-start:end-start));
        if(end==s.npos) break;
        start=end+1;
    }
    return result;
}
}

bool valid_personal_word(const PersonalWord& word) noexcept { return valid_personal_word_impl(word); }

std::wstring from_utf8(std::string_view s) {
    std::wstring out;
    for(std::size_t i=0;i<s.size();) {
        auto c=static_cast<unsigned char>(s[i++]);
        std::uint32_t cp=0, minimum=0;
        unsigned extra=0;
        if(c<0x80) cp=c;
        else if(c>=0xc2 && c<=0xdf) { cp=c&31; extra=1; minimum=0x80; }
        else if(c>=0xe0 && c<=0xef) { cp=c&15; extra=2; minimum=0x800; }
        else if(c>=0xf0 && c<=0xf4) { cp=c&7; extra=3; minimum=0x10000; }
        else throw std::invalid_argument("invalid UTF-8");
        if(i+extra>s.size()) throw std::invalid_argument("truncated UTF-8");
        for(unsigned k=0;k<extra;++k) {
            auto b=static_cast<unsigned char>(s[i++]);
            if((b&0xc0)!=0x80) throw std::invalid_argument("invalid UTF-8 continuation");
            cp=(cp<<6)|(b&63);
        }
        if(cp<minimum || cp>0x10ffff || (cp>=0xd800 && cp<=0xdfff))
            throw std::invalid_argument("invalid Unicode scalar");
        if(sizeof(wchar_t)==2 && cp>=0x10000) {
            cp-=0x10000;
            out.push_back(static_cast<wchar_t>(0xd800+(cp>>10)));
            out.push_back(static_cast<wchar_t>(0xdc00+(cp&1023)));
        } else out.push_back(static_cast<wchar_t>(cp));
    }
    return out;
}
std::string to_utf8(std::wstring_view s) {
    std::string out;
    for(std::size_t i=0;i<s.size();++i) {
        std::uint32_t cp=static_cast<std::uint32_t>(s[i]);
        if(high(s[i])) {
            if(i+1>=s.size() || !low(s[i+1])) throw std::invalid_argument("invalid UTF-16");
            cp=0x10000+((cp-0xd800)<<10)+(static_cast<std::uint32_t>(s[++i])-0xdc00);
        } else if(low(s[i]) || cp>0x10ffff) throw std::invalid_argument("invalid Unicode scalar");
        if(cp<0x80) out.push_back(static_cast<char>(cp));
        else if(cp<0x800) {
            out.push_back(static_cast<char>(0xc0|(cp>>6)));
            out.push_back(static_cast<char>(0x80|(cp&63)));
        } else if(cp<0x10000) {
            out.push_back(static_cast<char>(0xe0|(cp>>12)));
            out.push_back(static_cast<char>(0x80|((cp>>6)&63)));
            out.push_back(static_cast<char>(0x80|(cp&63)));
        } else {
            out.push_back(static_cast<char>(0xf0|(cp>>18)));
            out.push_back(static_cast<char>(0x80|((cp>>12)&63)));
            out.push_back(static_cast<char>(0x80|((cp>>6)&63)));
            out.push_back(static_cast<char>(0x80|(cp&63)));
        }
    }
    return out;
}

struct Dictionary::Impl {
    struct Entry { std::string key; std::wstring text; double frequency; std::vector<std::size_t> syllable_breaks; std::string syllables; };
    struct Node {
        std::array<int,26> children;
        std::vector<std::size_t> exact;
        std::vector<std::size_t> prefix;
        Node() { children.fill(-1); }
    };
    std::vector<Entry> entries;
    std::vector<Node> nodes{1};
    phonetic::Index phonetic_index;
    double total=1;
    void index_phonetics() {
        for(std::size_t id=0;id<entries.size();++id) {
            auto& entry=entries[id];
            if(entry.syllable_breaks.empty())
                phonetic::infer_breaks(entry.key,phonetic::scalar_count(entry.text),entry.syllable_breaks);
            entry.syllables.clear();
            if(entry.syllable_breaks.size()+1==phonetic::scalar_count(entry.text)&&
               phonetic::valid_breaks(entry.key,entry.syllable_breaks)) {
                std::size_t start=0;
                for(std::size_t i=0;i<=entry.syllable_breaks.size();++i) {
                    const auto end=i<entry.syllable_breaks.size()?entry.syllable_breaks[i]:entry.key.size();
                    if(i)entry.syllables.push_back(' ');
                    entry.syllables.append(entry.key,start,end-start);start=end;
                }
            }
            phonetic_index.add(id,entry.key,entry.syllable_breaks);
        }
        phonetic_index.finish([&](std::size_t a,std::size_t b){
            return entries[a].frequency!=entries[b].frequency?entries[a].frequency>entries[b].frequency:entries[a].text<entries[b].text;
        });
    }
    void index_prefixes() {
        // A completion finishes the final syllable already being typed. It
        // never predicts extra syllables/words from a shorter spelling. Apply
        // this before the bounded cache, so frequent long entries cannot evict
        // the ordinary word that actually matches the unfinished syllable.
        std::vector<std::size_t> depth(nodes.size());
        for(std::size_t i=0;i<nodes.size();++i)
            for(int child:nodes[i].children)if(child>=0)
                depth[static_cast<std::size_t>(child)]=depth[i]+1;
        auto ranked=[&](std::size_t a,std::size_t b) {
            if(entries[a].frequency!=entries[b].frequency) return entries[a].frequency>entries[b].frequency;
            return entries[a].text<entries[b].text;
        };
        for(std::size_t pos=nodes.size();pos>0;--pos) {
            auto& n=nodes[pos-1];
            std::sort(n.exact.begin(),n.exact.end(),ranked);
            n.prefix=n.exact;
            for(int child:n.children)if(child>=0) {
                const auto& p=nodes[static_cast<std::size_t>(child)].prefix;
                for(auto id:p) {
                    const auto& entry=entries[id];
                    const auto final_start=entry.syllable_breaks.empty()?0:entry.syllable_breaks.back();
                    if(!entry.syllables.empty()&&final_start<depth[pos-1])n.prefix.push_back(id);
                }
            }
            std::sort(n.prefix.begin(),n.prefix.end(),ranked);
            if(n.prefix.size()>prefix_limit)n.prefix.resize(prefix_limit);
        }
    }
    void index() {
        nodes.clear(); nodes.emplace_back(); total=1;
        for(std::size_t id=0;id<entries.size();++id) {
            std::size_t node=0;
            total+=entries[id].frequency;
            for(char c:entries[id].key) {
                auto index=static_cast<std::size_t>(c-'a');
                if(nodes[node].children[index]<0) {
                    const int next=static_cast<int>(nodes.size());
                    nodes[node].children[index]=next;
                    nodes.emplace_back();
                }
                node=static_cast<std::size_t>(nodes[node].children[index]);
            }
            nodes[node].exact.push_back(id);
        }
        index_phonetics();
        index_prefixes();
    }
};
Dictionary::Dictionary():impl_(std::make_unique<Impl>()) {}
Dictionary::~Dictionary()=default;
std::size_t Dictionary::size() const noexcept { return impl_->entries.size(); }
LoadReport Dictionary::load(const std::filesystem::path& path,bool use_syllable_sidecar) {
    if(path.extension()==".mlex") return load_binary(path);
    auto actual=path;
    const auto with_syllables=path.parent_path()/"syllables.tsv";
    if(use_syllable_sidecar&&path.filename()=="base.tsv" && std::filesystem::exists(with_syllables)) actual=with_syllables;
    if(std::filesystem::file_size(actual)>64*1024*1024) throw std::runtime_error("dictionary too large");
    std::ifstream input(actual,std::ios::binary);
    if(!input) throw std::runtime_error("dictionary unavailable");
    std::vector<Impl::Entry> parsed;
    std::string line;
    LoadReport report;
    std::size_t line_number=0;
    while(std::getline(input,line)) {
        ++line_number;
        if(line_number==1&&line.size()>=3&&line.compare(0,3,"\xef\xbb\xbf")==0)line.erase(0,3);
        if(!line.empty() && line.back()=='\r') line.pop_back();
        if(line.empty() || line[0]=='#') continue;
        const auto f=fields(line);
        try {
            if(f.size()<3 || f.size()>4 || f[0].empty() || f[0].size()>64 || f[1].empty() || f[1].size()>768)
                throw std::invalid_argument("invalid dictionary row");
            if(!std::all_of(f[0].begin(),f[0].end(),letter)) throw std::invalid_argument("invalid pinyin");
            std::size_t read=0;
            if(f[2].empty()||!std::all_of(f[2].begin(),f[2].end(),[](char c){return (c>='0'&&c<='9')||c=='.'||c=='e'||c=='E'||c=='+'||c=='-';}))
                throw std::invalid_argument("invalid frequency");
            const auto frequency=std::stod(std::string(f[2]),&read);
            if(read!=f[2].size() || !std::isfinite(frequency) || frequency<=0 || frequency>1e12)
                throw std::invalid_argument("invalid frequency");
            auto text=from_utf8(f[1]);
            if(std::any_of(text.begin(),text.end(),control))
                throw std::invalid_argument("control character");
            Impl::Entry entry{std::string(f[0]),std::move(text),frequency,{}};
            if(f.size()==4&&!f[3].empty()) {
                std::string joined;
                for(char c:f[3]) {
                    if(c==' ') {
                        if(joined.empty() || (!entry.syllable_breaks.empty() && entry.syllable_breaks.back()==joined.size()))
                            throw std::invalid_argument("invalid syllable separator");
                        entry.syllable_breaks.push_back(joined.size());
                    } else if(letter(c)) joined.push_back(c);
                    else throw std::invalid_argument("invalid syllable");
                }
                if(joined!=entry.key || (!entry.syllable_breaks.empty() && entry.syllable_breaks.back()>=joined.size()))
                    throw std::invalid_argument("inconsistent syllables");
                if(!phonetic::valid_breaks(entry.key,entry.syllable_breaks))
                    throw std::invalid_argument("unknown syllable");
            }
            parsed.push_back(std::move(entry)); ++report.loaded;
        } catch(const std::invalid_argument&) { ++report.rejected;if(!report.first_rejected_line)report.first_rejected_line=line_number; }
          catch(const std::out_of_range&) { ++report.rejected;if(!report.first_rejected_line)report.first_rejected_line=line_number; }
    }
    if(input.bad()) throw std::runtime_error("dictionary read failed");
    if(parsed.empty()) throw DictionaryFormatError(report.first_rejected_line);
    // Build a replacement before publishing, keeping the previous dictionary on failure.
    auto replacement=std::make_unique<Impl>();
    replacement->entries=impl_->entries;
    std::unordered_map<std::string,std::size_t> seen;
    for(std::size_t i=0;i<replacement->entries.size();++i) {
        const auto& e=replacement->entries[i]; seen[e.key+'\t'+to_utf8(e.text)]=i;
    }
    for(auto& e:parsed) {
        auto key=e.key+'\t'+to_utf8(e.text);
        auto it=seen.find(key);
        if(it==seen.end()) {
            seen.emplace(std::move(key),replacement->entries.size());
            replacement->entries.push_back(std::move(e));
        } else {
            auto& previous=replacement->entries[it->second];
            previous.frequency=std::max(previous.frequency,e.frequency);
            if(previous.syllable_breaks.empty()) previous.syllable_breaks=std::move(e.syllable_breaks);
        }
    }
    replacement->index(); impl_.swap(replacement);
    return report;
}

namespace {
std::uint64_t lexicon_checksum(const unsigned char* data,std::size_t size) {
    // Corruption check only. Download authenticity uses the source SHA256 manifest.
    std::uint64_t value=14695981039346656037ull;
    for(std::size_t i=0;i<size;++i) { value^=data[i]; value*=1099511628211ull; }
    return value;
}
struct BinaryWriter {
    std::vector<unsigned char> bytes;
    void number(std::uint64_t value,unsigned count=4) {
        for(unsigned i=0;i<count;++i) bytes.push_back(static_cast<unsigned char>(value>>(8*i)));
    }
    void string(std::string_view value) {
        number(value.size()); bytes.insert(bytes.end(),value.begin(),value.end());
    }
};
struct BinaryReader {
    const std::vector<unsigned char>& bytes;
    std::size_t pos=0;
    std::size_t end;
    void need(std::size_t count) const {
        if(pos>end || count>end-pos) throw std::runtime_error("truncated binary dictionary");
    }
    std::uint64_t number(unsigned count=4) {
        need(count); std::uint64_t value=0;
        for(unsigned i=0;i<count;++i) value|=static_cast<std::uint64_t>(bytes[pos++])<<(8*i);
        return value;
    }
    std::string string(std::size_t maximum) {
        const auto size=static_cast<std::size_t>(number());
        if(size>maximum) throw std::runtime_error("binary string too large");
        need(size); std::string value(reinterpret_cast<const char*>(bytes.data()+pos),size); pos+=size; return value;
    }
};
}
void Dictionary::save_binary(const std::filesystem::path& path) const {
    BinaryWriter out;
    const std::string magic="MNSLEX01"; out.bytes.insert(out.bytes.end(),magic.begin(),magic.end());
    out.number(impl_->entries.size()); out.number(impl_->nodes.size());
    for(const auto& e:impl_->entries) {
        out.string(e.key); out.string(to_utf8(e.text));
        std::uint64_t bits=0; static_assert(sizeof(bits)==sizeof(e.frequency));
        std::memcpy(&bits,&e.frequency,sizeof(bits)); out.number(bits,8);
        out.number(e.syllable_breaks.size()); for(auto p:e.syllable_breaks) out.number(p);
    }
    for(const auto& node:impl_->nodes) {
        for(int child:node.children) out.number(child<0?0xffffffffu:static_cast<std::uint32_t>(child));
        out.number(node.exact.size()); for(auto id:node.exact) out.number(id);
        out.number(node.prefix.size()); for(auto id:node.prefix) out.number(id);
    }
    out.number(lexicon_checksum(out.bytes.data(),out.bytes.size()),8);
    std::ofstream file(path,std::ios::binary|std::ios::trunc);
    file.write(reinterpret_cast<const char*>(out.bytes.data()),static_cast<std::streamsize>(out.bytes.size()));
    file.close();
    if(!file) throw std::runtime_error("binary dictionary write failed");
}
LoadReport Dictionary::load_binary(const std::filesystem::path& path) {
    const auto size=std::filesystem::file_size(path);
    if(size<24 || size>128*1024*1024) throw std::runtime_error("invalid binary dictionary size");
    std::vector<unsigned char> bytes(static_cast<std::size_t>(size));
    std::ifstream file(path,std::ios::binary);
    file.read(reinterpret_cast<char*>(bytes.data()),static_cast<std::streamsize>(size));
    if(!file) throw std::runtime_error("binary dictionary read failed");
    if(std::memcmp(bytes.data(),"MNSLEX01",8)!=0) throw std::runtime_error("unsupported binary dictionary version");
    BinaryReader trailer{bytes,bytes.size()-8,bytes.size()};
    if(lexicon_checksum(bytes.data(),bytes.size()-8)!=trailer.number(8))
        throw std::runtime_error("binary dictionary integrity check failed");
    BinaryReader in{bytes,8,bytes.size()-8};
    const auto entry_count=static_cast<std::size_t>(in.number());
    const auto node_count=static_cast<std::size_t>(in.number());
    if(entry_count==0 || entry_count>300000 || node_count==0 || node_count>2000000 ||
       entry_count*22ull+node_count*112ull>in.end-in.pos) throw std::runtime_error("invalid binary dictionary counts");
    auto replacement=std::make_unique<Impl>(); replacement->entries.reserve(entry_count);
    replacement->total=1;
    for(std::size_t id=0;id<entry_count;++id) {
        auto key=in.string(64); auto text=from_utf8(in.string(768));
        const auto bits=in.number(8); double frequency=0; std::memcpy(&frequency,&bits,sizeof(bits));
        if(key.empty() || !std::all_of(key.begin(),key.end(),letter) || text.empty() ||
           !std::isfinite(frequency) || frequency<=0 || frequency>1e12 ||
           std::any_of(text.begin(),text.end(),control))
            throw std::runtime_error("invalid binary entry");
        Impl::Entry e{std::move(key),std::move(text),frequency,{}};
        const auto boundaries=static_cast<std::size_t>(in.number());
        if(boundaries>e.key.size()) throw std::runtime_error("invalid binary boundaries");
        for(std::size_t i=0;i<boundaries;++i) {
            const auto boundary=static_cast<std::size_t>(in.number());
            if(boundary==0 || boundary>=e.key.size() || (!e.syllable_breaks.empty() && boundary<=e.syllable_breaks.back()))
                throw std::runtime_error("invalid binary boundary");
            e.syllable_breaks.push_back(boundary);
        }
        replacement->total+=frequency; replacement->entries.push_back(std::move(e));
    }
    replacement->nodes.resize(node_count);
    std::vector<bool> parent_seen(node_count,false); parent_seen[0]=true;
    for(std::size_t i=0;i<node_count;++i) {
        auto& node=replacement->nodes[i];
        if(!parent_seen[i]) throw std::runtime_error("unreachable binary trie node");
        for(auto& child:node.children) {
            const auto value=static_cast<std::uint32_t>(in.number());
            if(value==0xffffffffu) child=-1;
            else {
                if(value<=i || value>=node_count || parent_seen[value]) throw std::runtime_error("invalid binary trie edge");
                parent_seen[value]=true; child=static_cast<int>(value);
            }
        }
        auto read_indices=[&](std::vector<std::size_t>& ids,std::size_t maximum) {
            const auto count=static_cast<std::size_t>(in.number());
            if(count>maximum) throw std::runtime_error("invalid binary candidate count");
            in.need(count*4); ids.reserve(count);
            for(std::size_t j=0;j<count;++j) {
                const auto id=static_cast<std::size_t>(in.number());
                if(id>=entry_count) throw std::runtime_error("invalid binary candidate index");
                ids.push_back(id);
            }
        };
        read_indices(node.exact,entry_count); read_indices(node.prefix,prefix_limit);
    }
    if(in.pos!=in.end) throw std::runtime_error("unexpected binary dictionary trailer");
    replacement->index_phonetics();
    // Older MNSLEX01 files contain unrestricted prefix caches. Rebuild only
    // this derived index; entries and their exact/full readings stay intact.
    replacement->index_prefixes();
    impl_.swap(replacement); return {entry_count,0};
}

std::vector<Candidate> Dictionary::suggest(std::string_view raw,std::size_t limit,InputOptions options) const {
    return suggest_internal(raw,limit,options,false);
}
std::vector<Candidate> Dictionary::suggest_internal(std::string_view raw,std::size_t limit,InputOptions options,bool only_entries) const {
    if(raw.empty() || raw.size()>max_pinyin || limit==0) return {};
    limit=std::min<std::size_t>(limit,512);
    std::string pinyin;
    std::vector<std::size_t> raw_end(1,0);
    std::vector<std::size_t> boundaries;
    for(std::size_t i=0;i<raw.size();++i) {
        const auto c=raw[i];
        if(c=='\'') {
            if(pinyin.empty() || (i>0 && raw[i-1]=='\'')) return {};
            boundaries.push_back(pinyin.size());
            raw_end.back()=i+1;
        } else if(letter(c)) { pinyin.push_back(c); raw_end.push_back(i+1); }
        else return {};
    }
    if(pinyin.empty()) return {};
    struct Path { std::wstring text; double score=0; bool interjection=false; std::string syllables; bool whole_word=false; };
    auto reading=[](const Path& path,const Impl::Entry& entry) {
        if(entry.syllables.empty()||(!path.text.empty()&&path.syllables.empty()))return std::string{};
        return path.syllables.empty()?entry.syllables:path.syllables+' '+entry.syllables;
    };
    auto interjection=[](std::string_view spelling){return spelling=="m"||spelling=="n"||spelling=="ng"||spelling=="hm"||spelling=="hng";};
    std::vector<std::vector<Path>> paths(pinyin.size()+1);
    paths[0].push_back({});
    auto trim=[](std::vector<Path>& list) {
        if(list.empty())return;
        std::sort(list.begin(),list.end(),[](const Path& a,const Path& b){
            if(a.interjection!=b.interjection)return !a.interjection;
            return a.score!=b.score?a.score>b.score:a.text<b.text;
        });
        std::unordered_set<std::wstring> seen;
        std::vector<Path> unique; unique.reserve(beam_size);
        for(auto& p:list) if(seen.insert(p.text).second) {
            unique.push_back(std::move(p)); if(unique.size()==beam_size) break;
        }
        list=std::move(unique);
    };
    auto matches=[&](const Impl::Entry& e,std::size_t start,std::size_t stop) {
        for(auto boundary:boundaries) if(boundary>start && boundary<stop) {
            if(std::find(e.syllable_breaks.begin(),e.syllable_breaks.end(),boundary-start)==e.syllable_breaks.end()) return false;
        }
        // A trailing apostrophe declares the last syllable complete, not an arbitrary prefix.
        if(raw.back()=='\'' && stop==pinyin.size() && e.key.size()!=stop-start) return false;
        return true;
    };
    std::vector<Path> completions;
    std::vector<Path> word_completions;
    std::vector<Path> whole_words;
    for(std::size_t start=0;start<pinyin.size();++start) {
        if(only_entries&&start)break;
        if(paths[start].empty()) continue;
        trim(paths[start]);
        std::size_t node=0;
        for(std::size_t end=start;end<pinyin.size();++end) {
            const int child=impl_->nodes[node].children[static_cast<std::size_t>(pinyin[end]-'a')];
            if(child<0) break;
            node=static_cast<std::size_t>(child);
            const auto& n=impl_->nodes[node];
            for(auto id:n.exact) {
                const auto& e=impl_->entries[id];
                if(!matches(e,start,end+1)) continue;
                const double score=std::log(e.frequency/impl_->total);
                if(start==0 && end+1==pinyin.size()) whole_words.push_back({e.text,score,false,e.syllables,true});
                for(const auto& path:paths[start]) if(path.text.size()+e.text.size()<=max_draft)
                    paths[end+1].push_back({path.text+e.text,path.score+score,path.interjection||interjection(e.key),reading(path,e),path.text.empty()});
                if(paths[end+1].size()>beam_size*8) trim(paths[end+1]);
            }
            if(end+1==pinyin.size()) for(auto id:n.prefix) {
                const auto& e=impl_->entries[id];
                if(e.key.size()==end+1-start || !matches(e,start,end+1)) continue;
                const auto final_start=e.syllable_breaks.empty()?0:e.syllable_breaks.back();
                if(e.syllables.empty()||final_start>=end+1-start)continue;
                const auto missing=e.key.size()-(end+1-start);
                const double score=std::log(e.frequency/impl_->total)-4.0-static_cast<double>(missing)*0.35;
                auto& target=start==0?word_completions:completions;
                for(const auto& path:paths[start]) if(path.text.size()+e.text.size()<=max_draft)
                    target.push_back({path.text+e.text,path.score+score,path.interjection,reading(path,e),path.text.empty()});
                if(completions.size()>beam_size*8) trim(completions);
            }
        }
    }
    std::vector<Candidate> result;
    std::unordered_set<std::wstring> seen;
    auto append=[&](std::vector<Path>& list,std::size_t consumed,CandidateKind kind) {
        trim(list);
        for(auto& p:list) if(!p.text.empty() && seen.insert(p.text).second) {
            result.push_back({std::move(p.text),consumed,p.score,std::move(p.syllables),kind,p.whole_word});
            if(result.size()>=limit) return;
        }
    };
    std::vector<Path> ordinary_paths,interjection_paths;
    for(auto& path:paths.back())(path.interjection?interjection_paths:ordinary_paths).push_back(std::move(path));
    // A known complete dictionary entry precedes guesses assembled from shorter
    // entries. Keep this list independent of the bounded sentence beam so an
    // uncommon first character cannot erase a known word. Single-syllable and
    // multi-syllable exact entries still share their normal frequency ordering.
    std::sort(whole_words.begin(),whole_words.end(),[](const Path& a,const Path& b){
        return a.score!=b.score?a.score>b.score:a.text<b.text;
    });
    for(auto& p:whole_words) {
        if(result.size()>=limit) break;
        if(seen.insert(p.text).second) result.push_back({std::move(p.text),raw.size(),p.score,std::move(p.syllables),CandidateKind::Exact,true});
    }
    if(result.size()<limit) append(ordinary_paths,raw.size(),CandidateKind::Composed);
    // Preserve ordinary unfinished full-pinyin typing before looser matches.
    if(result.size()<limit) append(word_completions,raw.size(),CandidateKind::Completion);
    if(result.size()<limit&&(options.abbreviation||(options.fuzzy_mask&255u))) {
        // A syllable trie is traversed through preindexed full/initial/fuzzy
        // tokens. No input query enumerates the dictionary's entries.
        const auto transitions=impl_->phonetic_index.transitions(pinyin,boundaries,options);
        std::array<std::vector<std::vector<Path>>,2> alternate;
        for(auto& list:alternate)list.resize(pinyin.size()+1);
        std::array<std::vector<Path>,2> alternate_words;
        std::size_t budget=50000;
        for(std::size_t start=0;start<pinyin.size()&&budget;++start) {
            if(only_entries&&start)break;
            if(paths[start].empty()&&alternate[0][start].empty()&&alternate[1][start].empty())continue;
            for(auto& list:alternate)trim(list[start]);
            auto found=impl_->phonetic_index.matches(start,transitions,budget);
            std::sort(found.begin(),found.end(),[&](const auto& a,const auto& b){
                if(a.kind!=b.kind)return a.kind<b.kind;
                if(a.end!=b.end)return a.end>b.end;
                const auto& ea=impl_->entries[a.entry];const auto& eb=impl_->entries[b.entry];
                return ea.frequency!=eb.frequency?ea.frequency>eb.frequency:ea.text<eb.text;
            });
            std::array<std::array<unsigned,3>,max_pinyin+1> counts{};
            for(const auto& match:found) {
                const auto& entry=impl_->entries[match.entry];
                const double score=std::log(entry.frequency/impl_->total)-(match.kind==1?1.5:match.kind==2?3.0:0.0);
                if(start==0&&match.end==pinyin.size()&&match.kind)
                    alternate_words[match.kind-1].push_back({entry.text,score,false,entry.syllables,true});
                // A bounded sentence beam is separate from whole-word paging.
                if(++counts[match.end][match.kind]>32)continue;
                for(unsigned previous=0;previous<=2;++previous) {
                    const unsigned kind=std::max(previous,match.kind);if(!kind)continue;
                    const auto& source=previous?alternate[previous-1][start]:paths[start];
                    auto& target=alternate[kind-1][match.end];
                    for(const auto& path:source)if(path.text.size()+entry.text.size()<=max_draft)
                        target.push_back({path.text+entry.text,path.score+score,path.interjection||interjection(entry.key),reading(path,entry),path.text.empty()});
                    if(target.size()>beam_size*8)trim(target);
                }
            }
        }
        for(unsigned kind=0;kind<2&&result.size()<limit;++kind) {
            const auto category=kind?CandidateKind::Fuzzy:CandidateKind::Abbreviated;
            append(alternate[kind].back(),raw.size(),category);
            auto& words=alternate_words[kind];
            std::sort(words.begin(),words.end(),[](const auto& a,const auto& b){return a.score!=b.score?a.score>b.score:a.text<b.text;});
            for(auto& word:words) {
                if(result.size()>=limit)break;
                if(seen.insert(word.text).second)result.push_back({std::move(word.text),raw.size(),word.score,std::move(word.syllables),category,true});
            }
        }
    }
    if(result.size()<limit)append(interjection_paths,raw.size(),CandidateKind::Interjection);
    if(result.size()<limit)append(completions,raw.size(),CandidateKind::OtherCompletion);
    // Keep valid leading words selectable even when the remaining pinyin contains a typo.
    for(std::size_t pos=pinyin.size();pos>0 && result.size()<limit;--pos)
        if(pos<pinyin.size() && !paths[pos].empty()) append(paths[pos],raw_end[pos],CandidateKind::Partial);
    if(result.size()>limit) result.resize(limit);
    return result;
}

struct PersonalLexicon::Impl {
    Dictionary dictionary;
    std::vector<PersonalWord> words;
    std::unordered_map<std::wstring,std::unordered_map<std::string,std::uint32_t>> counts;
};
PersonalLexicon::PersonalLexicon(std::vector<PersonalWord> words):impl_(std::make_unique<Impl>()) {
    if(words.size()>max_personal_words)throw std::invalid_argument("too many personal entries");
    for(const auto& word:words)if(!valid_personal_word(word))throw std::invalid_argument("invalid personal entry");
    std::sort(words.begin(),words.end(),[](const auto& a,const auto& b){
        return a.text!=b.text?a.text<b.text:a.syllables<b.syllables;
    });
    for(auto& word:words) {
        if(!impl_->words.empty()&&impl_->words.back().text==word.text&&impl_->words.back().syllables==word.syllables)
            impl_->words.back().uses=std::max(impl_->words.back().uses,word.uses);
        else impl_->words.push_back(std::move(word));
    }
    auto& entries=impl_->dictionary.impl_->entries;
    entries.reserve(impl_->words.size());
    for(const auto& word:impl_->words) {
        Dictionary::Impl::Entry entry{{},word.text,static_cast<double>(word.uses),{}, {}};
        for(char c:word.syllables) {
            if(c==' ')entry.syllable_breaks.push_back(entry.key.size());
            else entry.key.push_back(c);
        }
        impl_->counts[word.text].emplace(word.syllables,word.uses);
        entries.push_back(std::move(entry));
    }
    impl_->dictionary.impl_->index();
}
PersonalLexicon::~PersonalLexicon()=default;
const std::vector<PersonalWord>& PersonalLexicon::words() const noexcept { return impl_->words; }
std::uint32_t PersonalLexicon::uses(const Candidate& candidate) const noexcept {
    const auto word=impl_->counts.find(candidate.text);
    if(word==impl_->counts.end())return 0;
    const auto reading=word->second.find(candidate.syllables);
    return reading==word->second.end()?0:reading->second;
}
std::vector<Candidate> PersonalLexicon::suggest(std::string_view pinyin,InputOptions options) const {
    // Personal entries are a bounded overlay, not a second sentence decoder.
    return impl_->dictionary.suggest_internal(pinyin,512,options,true);
}

DraftSession::DraftSession(std::shared_ptr<const Dictionary> dictionary):dictionary_(std::move(dictionary)) {}
void DraftSession::set_english_suggestions(bool enabled) noexcept {
    if(english_suggestions_enabled_==enabled)return;
    english_suggestions_enabled_=enabled;++revision_;reset_gesture();
}
std::vector<std::wstring> DraftSession::english_completions() const {
    std::vector<std::wstring> result;
    if(!english_suggestions_enabled_||!pinyin_.empty()||pending_)return result;
    const auto prefix=english::prefix(draft_,caret_);
    for(std::size_t i=0;i<3;++i){const auto word=english::at(prefix,i);if(!word)break;result.push_back(english::complete(prefix,word));}
    return result;
}
void DraftSession::reset_gesture() noexcept {
    spaces_=0;last_space_=0;space_gesture_=SpaceGesture::None;space_anchor_=0;
}
void DraftSession::refresh() {
    // Keep the exact letters for explicit English confirmation. ASCII folding
    // changes neither apostrophe boundaries nor candidate consumption offsets.
    auto query=pinyin_;
    for(auto& c:query)if(c>='A'&&c<='Z')c=static_cast<char>(c-'A'+'a');
    candidates_=dictionary_?dictionary_->suggest(query,512,options_):std::vector<Candidate>{}; selected_=0;
    if(personalization_enabled_&&!pinyin_.empty()&&(personal_||temporary_personal_)) {
    struct Ranked {Candidate candidate;std::size_t base_rank;std::uint32_t uses=0;};
    std::vector<Ranked> ranked;
    std::unordered_map<std::wstring,std::unordered_map<std::string,std::size_t>> positions;
    std::array<std::size_t,8> category_ranks{};
    for(auto& candidate:candidates_) {
        positions[candidate.text].emplace(candidate.syllables,ranked.size());
        const auto ordinal=category_ranks[static_cast<unsigned>(candidate.kind)]++;
        ranked.push_back({std::move(candidate),ordinal});
    }
    for(const auto* overlay:{personal_.get(),temporary_personal_.get()})if(overlay) {
        for(auto& candidate:overlay->suggest(query,options_)) {
            if(!candidate.whole_word)continue;
            auto& readings=positions[candidate.text];
            const auto found=readings.find(candidate.syllables);
            if(found==readings.end()) {
                readings.emplace(candidate.syllables,ranked.size());
                ranked.push_back({std::move(candidate),std::numeric_limits<std::size_t>::max()});
            } else {
                auto& previous=ranked[found->second];
                if(candidate.kind<previous.candidate.kind) {
                    previous.candidate=std::move(candidate);
                    previous.base_rank=std::numeric_limits<std::size_t>::max();
                }
            }
        }
    }
    for(auto& entry:ranked) {
        const auto saved=personal_?personal_->uses(entry.candidate):0;
        const auto provisional=temporary_personal_?temporary_personal_->uses(entry.candidate):0;
        entry.uses=std::min(max_personal_uses,saved+provisional);
    }
    // One observation makes the word selectable after at most two unpersonalized
    // exact matches. Repeated use ranks it ahead of ordinary peers. Match class
    // remains the primary key, so even a frequent abbreviation cannot beat full pinyin.
    auto promotion=[](const Ranked& entry) {
        if(entry.candidate.kind==CandidateKind::Completion||entry.candidate.kind==CandidateKind::OtherCompletion)
            return entry.base_rank==std::numeric_limits<std::size_t>::max()?3:1;
        if(entry.uses>=2)return 0;
        if(entry.base_rank<2)return 1;
        return entry.uses?2:3;
    };
    std::sort(ranked.begin(),ranked.end(),[&](const auto& a,const auto& b) {
        if(a.candidate.kind!=b.candidate.kind)return a.candidate.kind<b.candidate.kind;
        const auto pa=promotion(a),pb=promotion(b);
        if(pa!=pb)return pa<pb;
        if(pa==0&&a.uses!=b.uses)return a.uses>b.uses;
        if(a.base_rank!=b.base_rank)return a.base_rank<b.base_rank;
        if(a.base_rank==std::numeric_limits<std::size_t>::max()&&a.uses!=b.uses)return a.uses>b.uses;
        if(a.candidate.text!=b.candidate.text)return a.candidate.text<b.candidate.text;
        return a.candidate.syllables<b.candidate.syllables;
    });
    candidates_.clear();
    std::unordered_set<std::wstring> seen;
    for(auto& entry:ranked)if(seen.insert(entry.candidate.text).second) {
        candidates_.push_back(std::move(entry.candidate));if(candidates_.size()==512)break;
    }
    }
    if(!pinyin_.empty()) {
        Candidate raw{std::wstring(pinyin_.begin(),pinyin_.end()),pinyin_.size(),0,{},CandidateKind::Raw,false};
        // Even an imported literal with identical display text has one explicit
        // raw action. Chinese alternatives retain their relative order.
        candidates_.erase(std::remove_if(candidates_.begin(),candidates_.end(),[&](const auto& c){return c.text==raw.text;}),candidates_.end());
        const auto position=std::min<std::size_t>(8,candidates_.size());
        candidates_.insert(candidates_.begin()+position,std::move(raw));
        if(candidates_.size()>512)candidates_.pop_back();
    }
}
void DraftSession::set_options(InputOptions options) {
    options.fuzzy_mask&=255u;
    if(options_.abbreviation==options.abbreviation&&options_.fuzzy_mask==options.fuzzy_mask)return;
    auto next=*this;next.options_=options;next.refresh();next.reset_gesture();++next.revision_;
    *this=std::move(next);
}
void DraftSession::set_personal_lexicon(std::shared_ptr<const PersonalLexicon> lexicon) {
    // The caller retries the latest snapshot before subsequent physical keys.
    // Never change visible candidate numbers or a partially entered gesture
    // just because an asynchronous disk snapshot became available.
    if(personal_==lexicon||!pinyin_.empty())return;
    personal_=std::move(lexicon);
}
void DraftSession::set_personalization_enabled(bool enabled) {
    if(personalization_enabled_==enabled)return;
    auto next=*this;next.personalization_enabled_=enabled;
    if(!enabled) {
        next.clear_observations();
        if(next.pending_)next.pending_->personal_words.clear();
    }
    // An explicit toggle takes effect for recording immediately, but the numbers
    // currently displayed stay attached to the same words until the next edit.
    *this=std::move(next);
}
void DraftSession::clear_observations() noexcept {
    observations_.clear();selection_run_.clear();temporary_personal_.reset();
}
void DraftSession::observe(const Candidate& candidate) {
    if(!personalization_enabled_)return;
    PersonalWord chosen{candidate.text,candidate.syllables,1};
    if(!valid_personal_word(chosen)) {selection_run_.clear();return;}
    auto record=[&](const PersonalWord& value) {
        const auto found=std::find_if(observations_.begin(),observations_.end(),[&](const auto& word){
            return word.text==value.text&&word.syllables==value.syllables;
        });
        if(found!=observations_.end()) {if(found->uses<max_personal_uses)++found->uses;}
        else if(observations_.size()<max_observations)observations_.push_back(value);
    };
    record(chosen);
    selection_run_.push_back(std::move(chosen));
    while(selection_run_.size()>4)selection_run_.erase(selection_run_.begin());
    PersonalWord combined=selection_run_.back();
    for(std::size_t i=selection_run_.size()-1;i>0;--i) {
        const auto& previous=selection_run_[i-1];
        combined.text=previous.text+combined.text;
        combined.syllables=previous.syllables+' '+combined.syllables;
        // Adjacent selections are not evidence that a whole sentence is one
        // word. Learn short compounds (e.g. 九 + 班) but stop at four Han
        // characters. A longer explicitly selected dictionary word can still
        // be remembered by record(chosen) above.
        if(!valid_personal_word(combined)||han_count(combined.text)>4)break;
        record(combined); // Two or more distinct selected spans, never the same span twice.
    }
    temporary_personal_=std::make_shared<PersonalLexicon>(observations_);
}
bool DraftSession::choose(std::size_t index) {
    if(index>=candidates_.size()) return false;
    if(candidates_[index].kind==CandidateKind::Raw)return accept_raw_internal();
    auto c=candidates_[index];
    if(c.consumed==0 || c.consumed>pinyin_.size() || draft_.size()+c.text.size()>max_draft) return false;
    if(caret_!=draft_.size())clear_observations();
    draft_.insert(caret_,c.text); caret_+=c.text.size(); pinyin_.erase(0,c.consumed);
    observe(c);
    refresh(); ++revision_; reset_gesture(); return true;
}
bool DraftSession::accept_raw_internal() {
    if(pinyin_.empty())return true;
    if(pending_||draft_.size()+pinyin_.size()>max_draft)return false;
    const std::wstring raw(pinyin_.begin(),pinyin_.end());
    if(caret_!=draft_.size())clear_observations();
    selection_run_.clear();
    draft_.insert(caret_,raw);caret_+=raw.size();pinyin_.clear();
    refresh();++revision_;reset_gesture();return true;
}
bool DraftSession::accept_raw() {
    if(pinyin_.empty())return true;
    if(pending_||draft_.size()+pinyin_.size()>max_draft)return false;
    auto next=*this;
    if(!next.accept_raw_internal())return false;
    *this=std::move(next);return true;
}
bool DraftSession::wants(const Key& k) const noexcept {
    if(pending_ || !dictionary_ || dictionary_->size()==0) return false;
    if(k.kind==KeyKind::Letter) return (k.character>=L'a' && k.character<=L'z') ||
        (k.character>=L'A' && k.character<=L'Z') || (k.character==L'\'' && !pinyin_.empty());
    if(k.kind==KeyKind::Literal)return k.character>=32&&k.character<=126;
    if(k.kind==KeyKind::EnglishSpace)return true;
    if(k.kind==KeyKind::CompleteEnglish)return english_suggestions_enabled_&&pinyin_.empty()&&
        k.character<3&&english::at(english::prefix(draft_,caret_),static_cast<std::size_t>(k.character));
    if(k.kind==KeyKind::PageUp||k.kind==KeyKind::PageDown)return !candidates_.empty();
    return !empty();
}
KeyResult DraftSession::prepare(bool learn) {
    if(pending_) return {};
    // A commit never implicitly chooses a Chinese candidate. Selection or an
    // explicit raw confirmation must first resolve the conversion buffer.
    if(!pinyin_.empty())return {true,false,{}};
    if(draft_.empty()) return {};
    pending_=Commit{next_commit_++,draft_,learn,personalization_enabled_?observations_:std::vector<PersonalWord>{}}; reset_gesture();
    return {true,true,pending_};
}
KeyResult DraftSession::key(const Key& k) {
    if(!wants(k)) return {};
    // Work on a bounded snapshot. Allocation failures must not leave an unseen
    // pending commit or partially consume a candidate in the live input session.
    auto next=*this;
    auto result=next.apply_key(k);
    static_assert(std::is_nothrow_move_assignable<DraftSession>::value,
                  "publishing an input transaction must not allocate");
    *this=std::move(next);
    return result;
}
KeyResult DraftSession::apply_key(const Key& k) {
    KeyResult result{true,false,{}};
    if(k.kind!=KeyKind::Space&&k.kind!=KeyKind::EnglishSpace) reset_gesture();
    switch(k.kind) {
    case KeyKind::CompleteEnglish: {
        const auto prefix=english::prefix(draft_,caret_);const auto word=english::at(prefix,static_cast<std::size_t>(k.character));
        if(!word)break;
        const auto completed=english::complete(prefix,word);const auto tail=completed.substr(prefix.size());
        if(draft_.size()+tail.size()>max_draft)break;
        if(caret_!=draft_.size())clear_observations();
        selection_run_.clear();draft_.insert(caret_,tail);caret_+=tail.size();result.changed=true;
        break;
    }
    case KeyKind::Letter:
        if(pinyin_.size()<max_pinyin && (k.character!=L'\'' || pinyin_.back()!='\'')) {
            pinyin_.push_back(static_cast<char>(k.character)); refresh(); result.changed=true;
        }
        break;
    case KeyKind::Space:
        if(k.repeat) { reset_gesture(); break; }
        if(!pinyin_.empty()) { result.changed=choose(selected_); break; }
        if(space_gesture_!=SpaceGesture::Chinese || spaces_==0 || k.timestamp_ms<last_space_ || k.timestamp_ms-last_space_>650) spaces_=1;
        else ++spaces_;
        space_gesture_=SpaceGesture::Chinese;
        last_space_=k.timestamp_ms;
        if(spaces_==3) return prepare(true);
        break;
    case KeyKind::EnglishSpace: {
        const bool consecutive=!k.repeat&&pinyin_.empty()&&space_gesture_==SpaceGesture::English&&
            spaces_>0&&spaces_<=2&&k.timestamp_ms>=last_space_&&k.timestamp_ms-last_space_<=650&&
            caret_==space_anchor_+spaces_&&caret_<=draft_.size()&&
            std::all_of(draft_.begin()+space_anchor_,draft_.begin()+caret_,[](wchar_t c){return c==L' ';});
        if(consecutive&&spaces_==2) {
            draft_.erase(space_anchor_,2);caret_=space_anchor_;++revision_;reset_gesture();
            if(draft_.empty())return {true,true,{}};
            return prepare(true);
        }
        // Reserve the complete operation before sealing letters. A full draft
        // cannot lose letters or later retract somebody else's trailing spaces.
        if(draft_.size()+pinyin_.size()+1>max_draft) {reset_gesture();break;}
        if(!consecutive)reset_gesture();
        if(!accept_raw_internal())break;
        if(caret_!=draft_.size())clear_observations();
        selection_run_.clear();
        if(!consecutive)space_anchor_=caret_;
        draft_.insert(caret_++,1,L' ');result.changed=true;
        if(k.repeat)reset_gesture();
        else {++spaces_;space_gesture_=SpaceGesture::English;last_space_=k.timestamp_ms;}
        break;
    }
    case KeyKind::Literal:
        if(draft_.size()+pinyin_.size()+1<=max_draft&&accept_raw_internal()) {
            if(caret_!=draft_.size())clear_observations();
            selection_run_.clear();draft_.insert(caret_++,1,k.character);result.changed=true;
        }
        break;
    case KeyKind::Digit:
        if(!pinyin_.empty()) {
            if(k.character>=L'1' && k.character<=L'9')
                result.changed=choose((selected_/9)*9+static_cast<std::size_t>(k.character-L'1'));
        } else if(k.character>=L'0' && k.character<=L'9' && draft_.size()<max_draft) {
            if(caret_!=draft_.size())clear_observations();
            selection_run_.clear();
            draft_.insert(caret_++,1,k.character); result.changed=true;
        }
        break;
    case KeyKind::Backspace:
        if(!pinyin_.empty()) { pinyin_.pop_back(); refresh(); result.changed=true; }
        else if(caret_>0) { auto p=before(draft_,caret_); draft_.erase(p,caret_-p); caret_=p;clear_observations();result.changed=true; }
        break;
    case KeyKind::Delete:
        if(!pinyin_.empty()) { pinyin_.clear();clear_observations();refresh();result.changed=true; }
        else if(caret_<draft_.size()) { draft_.erase(caret_,after(draft_,caret_)-caret_);clear_observations();result.changed=true; }
        break;
    case KeyKind::Up:
        if(!candidates_.empty()) { selected_=(selected_+candidates_.size()-1)%candidates_.size(); result.changed=true; }
        break;
    case KeyKind::Down:
        if(!candidates_.empty()) { selected_=(selected_+1)%candidates_.size(); result.changed=true; }
        break;
    case KeyKind::PageUp:
    case KeyKind::PageDown:
        if(!candidates_.empty()) {
            const auto page=selected_/9;
            const auto next=k.kind==KeyKind::PageUp?(page?page-1:0):std::min(page+1,(candidates_.size()-1)/9);
            if(next!=page){selected_=next*9;result.changed=true;}
        }
        break;
    case KeyKind::Left:
    case KeyKind::Right:
        if(!pinyin_.empty() && !choose(selected_)) break;
        selection_run_.clear();
        caret_=k.kind==KeyKind::Left?before(draft_,caret_):after(draft_,caret_); result.changed=true;
        break;
    case KeyKind::Enter:
        if(!pinyin_.empty()) {result.changed=accept_raw_internal();break;}
        return prepare(false);
    case KeyKind::Escape:
        clear_observations();
        if(!pinyin_.empty()) { pinyin_.clear(); refresh(); }
        else { draft_.clear(); caret_=0; }
        result.changed=true; break;
    case KeyKind::Punctuation:
        if(!pinyin_.empty() && !choose(selected_)) break;
        if(draft_.size()<max_draft && k.character>=32) {
            if(caret_!=draft_.size())clear_observations();
            selection_run_.clear();draft_.insert(caret_++,1,k.character);result.changed=true;
        }
        break;
    }
    if(result.changed) ++revision_;
    return result;
}
bool DraftSession::acknowledge(std::uint64_t id,CommitResult result) {
    if(!pending_ || pending_->id!=id) return false;
    if(result==CommitResult::Unknown) { recovery_=std::move(pending_->text); uncertain_=true; }
    if(result!=CommitResult::NotWritten) { draft_.clear(); pinyin_.clear(); candidates_.clear();caret_=0;selected_=0;clear_observations(); }
    pending_.reset(); reset_gesture(); ++revision_; return true;
}
std::wstring DraftSession::preview() const {
    auto result=draft_;
    result.insert(caret_,from_utf8(pinyin_));
    return result;
}
}
