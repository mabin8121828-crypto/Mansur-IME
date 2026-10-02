// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <unordered_set>
#include <utility>
#include <vector>

namespace mansur::phonetic {
// Syllable inventory from the already attributed Apache-2.0 AOSP dictionary
// data (data/generated/NOTICE-AOSP.txt); this is data, not an imported input engine.
inline const std::vector<std::string>& syllables() {
    static const auto result=[] {
        const std::string_view names=
            "a ai an ang ao ba bai ban bang bao bei ben beng bi bian biao bie bin bing bo bu "
            "ca cai can cang cao ce cen ceng cha chai chan chang chao che chen cheng chi chong chou chu chua chuai chuan chuang chui chun chuo ci cong cou cu cuan cui cun cuo "
            "da dai dan dang dao de dei den deng di dia dian diao die ding diu dong dou du duan dui dun duo e ei en eng er "
            "fa fan fang fei fen feng fiao fo fou fu ga gai gan gang gao ge gei gen geng gong gou gu gua guai guan guang gui gun guo "
            "ha hai han hang hao he hei hen heng hm hng hong hou hu hua huai huan huang hui hun huo "
            "ji jia jian jiang jiao jie jin jing jiong jiu ju juan jue jun ka kai kan kang kao ke kei ken keng kong kou ku kua kuai kuan kuang kui kun kuo "
            "la lai lan lang lao le lei leng li lia lian liang liao lie lin ling liu lo long lou lu luan lue lun luo lv lve "
            "m ma mai man mang mao me mei men meng mi mian miao mie min ming miu mo mou mu n na nai nan nang nao ne nei nen neng ng ni nian niang niao nie nin ning niu nong nou nu nuan nue nuo nv nve o ou "
            "pa pai pan pang pao pei pen peng pi pian piao pie pin ping po pou pu qi qia qian qiang qiao qie qin qing qiong qiu qu quan que qun "
            "ran rang rao re ren reng ri rong rou ru ruan rui run ruo sa sai san sang sao se sen seng sha shai shan shang shao she shei shen sheng shi shou shu shua shuai shuan shuang shui shun shuo si song sou su suan sui sun suo "
            "ta tai tan tang tao te tei teng ti tian tiao tie ting tong tou tu tuan tui tun tuo wa wai wan wang wei wen weng wo wu "
            "xi xia xian xiang xiao xie xin xing xiong xiu xu xuan xue xun ya yan yang yao ye yi yin ying yo yong you yu yuan yue yun "
            "za zai zan zang zao ze zei zen zeng zha zhai zhan zhang zhao zhe zhei zhen zheng zhi zhong zhou zhu zhua zhuai zhuan zhuang zhui zhun zhuo zi zong zou zu zuan zui zun zuo";
        std::vector<std::string> out;
        for(std::size_t pos=0;pos<names.size();) {
            const auto end=names.find(' ',pos);out.emplace_back(names.substr(pos,end==names.npos?names.size()-pos:end-pos));
            if(end==names.npos)break;pos=end+1;
        }
        return out;
    }();
    return result;
}
inline int syllable_id(std::string_view value) {
    const auto& names=syllables();const auto found=std::lower_bound(names.begin(),names.end(),value);
    return found!=names.end()&&*found==value?static_cast<int>(found-names.begin()):-1;
}
inline std::size_t scalar_count(std::wstring_view text) noexcept {
    std::size_t count=0;
    for(auto c:text)if(c<0xdc00||c>0xdfff)++count;
    return count;
}
inline bool infer_breaks(std::string_view key,std::size_t count,std::vector<std::size_t>& breaks) {
    if(!count||count>64||key.size()>64||count>key.size())return false;
    std::array<std::array<bool,65>,65> failed{};
    std::vector<std::size_t> trial;
    bool allow_interjections=count==1;
    std::function<bool(std::size_t,std::size_t)> visit=[&](std::size_t pos,std::size_t left) {
        if(!left)return pos==key.size();
        if(key.size()-pos<left||key.size()-pos>left*6||failed[pos][left])return false;
        // Prefer a longer complete syllable when the character count is ambiguous.
        for(std::size_t n=std::min<std::size_t>(6,key.size()-pos);n>0;--n) {
            if(syllable_id(key.substr(pos,n))<0)continue;
            const auto syllable=key.substr(pos,n);
            if(!allow_interjections&&(syllable=="m"||syllable=="n"||syllable=="ng"||syllable=="hm"||syllable=="hng"))continue;
            if(pos+n<key.size())trial.push_back(pos+n);
            if(visit(pos+n,left-1)){breaks=trial;return true;}
            if(pos+n<key.size())trial.pop_back();
        }
        failed[pos][left]=true;return false;
    };
    if(visit(0,count))return true;
    if(allow_interjections)return false;
    failed={};trial.clear();allow_interjections=true;return visit(0,count);
}
inline bool valid_breaks(std::string_view key,const std::vector<std::size_t>& breaks) {
    std::size_t start=0;
    for(std::size_t i=0;i<=breaks.size();++i) {
        const auto end=i<breaks.size()?breaks[i]:key.size();
        if(end<=start||end>key.size()||syllable_id(key.substr(start,end-start))<0)return false;
        start=end;
    }
    return true;
}

class Index {
    struct Token {std::uint16_t syllable;std::uint32_t mask;bool abbreviated;};
    struct TokenNode {
        std::array<int,26> children;std::vector<Token> tokens;
        TokenNode(){children.fill(-1);}
    };
    struct WordNode {
        std::vector<std::pair<std::uint16_t,std::size_t>> children;
        std::vector<std::size_t> entries;
    };
    std::vector<TokenNode> tokens_{1};
    std::vector<WordNode> words_{1};
    void token(std::string_view text,Token value) {
        std::size_t node=0;
        for(char c:text) {
            auto& edge=tokens_[node].children[static_cast<std::size_t>(c-'a')];
            if(edge<0){const auto next=static_cast<int>(tokens_.size());edge=next;tokens_.emplace_back();node=static_cast<std::size_t>(next);}
            else node=static_cast<std::size_t>(edge);
        }
        auto& list=tokens_[node].tokens;
        if(std::none_of(list.begin(),list.end(),[&](const Token& t){return t.syllable==value.syllable&&t.mask==value.mask&&t.abbreviated==value.abbreviated;}))list.push_back(value);
    }
    static std::pair<std::string,std::uint32_t> initial_alternate(std::string_view s) {
        for(const auto& pair:std::array<std::pair<char,std::uint32_t>,3>{{{'z',1},{'c',2},{'s',4}}}) {
            if(s[0]!=pair.first)continue;
            if(s.size()>1&&s[1]=='h')return {std::string(1,s[0])+std::string(s.substr(2)),pair.second};
            return {std::string(1,s[0])+"h"+std::string(s.substr(1)),pair.second};
        }
        if(s[0]=='n'||s[0]=='l')return {std::string(1,s[0]=='n'?'l':'n')+std::string(s.substr(1)),8};
        if(s[0]=='f'||s[0]=='h')return {std::string(1,s[0]=='f'?'h':'f')+std::string(s.substr(1)),16};
        return {};
    }
    static std::pair<std::string,std::uint32_t> final_alternate(std::string_view s) {
        for(const auto& pair:std::array<std::pair<std::string_view,std::uint32_t>,3>{{{"an",32},{"en",64},{"in",128}}}) {
            const std::string long_form=std::string(pair.first)+"g";
            if(s.size()>=long_form.size()&&s.substr(s.size()-long_form.size())==long_form)return {std::string(s.substr(0,s.size()-1)),pair.second};
            if(s.size()>=pair.first.size()&&s.substr(s.size()-pair.first.size())==pair.first)return {std::string(s)+"g",pair.second};
        }
        return {};
    }
public:
    struct Transition {std::uint16_t syllable;std::size_t end;unsigned kind;};
    struct Match {std::size_t entry,end;unsigned kind;};
    Index() {
        const auto& names=syllables();
        for(std::size_t i=0;i<names.size();++i) {
            std::vector<std::pair<std::string,std::uint32_t>> forms{{names[i],0}};
            auto initial=initial_alternate(names[i]);if(!initial.first.empty())forms.push_back(std::move(initial));
            const auto initial_count=forms.size();
            for(std::size_t j=0;j<initial_count;++j) {
                auto final=final_alternate(forms[j].first);
                if(!final.first.empty()){final.second|=forms[j].second;forms.push_back(std::move(final));}
            }
            for(const auto& form:forms) {
                token(form.first,{static_cast<std::uint16_t>(i),form.second,false});
                if(form.first.size()>1)token(form.first.substr(0,1),{static_cast<std::uint16_t>(i),form.second,true});
                if(form.first.size()>2&&form.first[1]=='h'&&(form.first[0]=='z'||form.first[0]=='c'||form.first[0]=='s'))
                    token(form.first.substr(0,2),{static_cast<std::uint16_t>(i),form.second,true});
            }
        }
    }
    void add(std::size_t id,std::string_view key,const std::vector<std::size_t>& breaks) {
        if(!valid_breaks(key,breaks))return;
        std::size_t node=0,start=0;
        for(std::size_t i=0;i<=breaks.size();++i) {
            const auto end=i<breaks.size()?breaks[i]:key.size();
            const auto syllable=static_cast<std::uint16_t>(syllable_id(key.substr(start,end-start)));start=end;
            auto& edges=words_[node].children;
            const auto found=std::lower_bound(edges.begin(),edges.end(),syllable,[](const auto& e,auto s){return e.first<s;});
            if(found!=edges.end()&&found->first==syllable)node=found->second;
            else {const auto next=words_.size();edges.insert(found,{syllable,next});words_.emplace_back();node=next;}
        }
        words_[node].entries.push_back(id);
    }
    template<class Ranked> void finish(Ranked ranked) {
        for(auto& word:words_)std::sort(word.entries.begin(),word.entries.end(),ranked);
    }
    std::vector<std::vector<Transition>> transitions(std::string_view input,const std::vector<std::size_t>& boundaries,InputOptions options) const {
        std::vector<std::vector<Transition>> result(input.size());
        for(std::size_t start=0;start<input.size();++start) {
            std::size_t node=0;
            for(std::size_t end=start;end<input.size()&&end-start<6;++end) {
                if(end>start&&std::binary_search(boundaries.begin(),boundaries.end(),end))break;
                const auto next=tokens_[node].children[static_cast<std::size_t>(input[end]-'a')];if(next<0)break;
                node=static_cast<std::size_t>(next);
                for(const auto& t:tokens_[node].tokens)if((options.abbreviation||!t.abbreviated)&&(t.mask&options.fuzzy_mask)==t.mask)
                    result[start].push_back({t.syllable,end+1,t.mask?2u:(t.abbreviated?1u:0u)});
            }
            auto& list=result[start];
            std::sort(list.begin(),list.end(),[](const auto& a,const auto& b){return a.kind!=b.kind?a.kind<b.kind:(a.end!=b.end?a.end>b.end:a.syllable<b.syllable);});
            list.erase(std::unique(list.begin(),list.end(),[](const auto& a,const auto& b){return a.kind==b.kind&&a.end==b.end&&a.syllable==b.syllable;}),list.end());
        }
        return result;
    }
    std::vector<Match> matches(std::size_t start,const std::vector<std::vector<Transition>>& transitions,std::size_t& budget) const {
        struct State {std::size_t node,pos;unsigned kind;};
        std::vector<State> pending{{0,start,0}};
        std::unordered_set<std::uint64_t> seen;
        std::vector<Match> result;
        std::size_t examined=0;
        while(!pending.empty()&&budget&&examined<2048) {
            const auto state=pending.back();pending.pop_back();++examined;
            const auto signature=(static_cast<std::uint64_t>(state.node)<<16)|(state.pos<<2)|state.kind;
            if(!seen.insert(signature).second)continue;
            const auto& node=words_[state.node];
            for(auto id:node.entries) {
                if(!budget||result.size()>=2048)break;--budget;
                result.push_back({id,state.pos,state.kind});
            }
            if(state.pos>=transitions.size())continue;
            // Push alternate forms first so exact full syllables are visited first.
            const auto& choices=transitions[state.pos];
            for(auto it=choices.rbegin();it!=choices.rend()&&budget;++it) {
                --budget;
                const auto edge=std::lower_bound(node.children.begin(),node.children.end(),it->syllable,[](const auto& e,auto s){return e.first<s;});
                if(edge!=node.children.end()&&edge->first==it->syllable&&pending.size()<2048)
                    pending.push_back({edge->second,it->end,std::max(state.kind,it->kind)});
            }
        }
        return result;
    }
};
}
