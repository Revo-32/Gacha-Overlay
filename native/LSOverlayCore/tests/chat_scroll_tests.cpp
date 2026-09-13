#include "chat_scroll.hpp"
#include <iostream>
#include <limits>
int main() {
    unsigned checks = 0;
    auto check = [&](bool value) { ++checks; if (!value) throw std::runtime_error("Chat anchor assertion failed"); };
    try {
        core::ChatScroll scroll;
        scroll.setRows({{"a",100},{"b",100},{"c",100}},100);
        check(scroll.offset() == 200 && scroll.following());
        scroll.scroll(-150); check(scroll.anchor().id == "a" && scroll.anchor().within == 50 && !scroll.following());
        scroll.setRows({{"a",100},{"b",100},{"c",100},{"d",100}},100);
        check(scroll.offset() == 50 && scroll.unread() == 1);
        scroll.setRows({{"a",150},{"b",150},{"c",150},{"d",150}},100);
        check(scroll.offset() == 50 && scroll.unread() == 1);
        scroll.setRows({{"b",150},{"c",150},{"d",150}},100);
        check(scroll.anchor().id == "b" && scroll.offset() == 0);
        scroll.scroll(180); check(scroll.anchor().id == "c" && scroll.anchor().within == 30);
        scroll.setRows({{"c",150},{"d",150}},100);
        check(scroll.offset() == 30 && scroll.anchor().id == "c");
        scroll.scroll(std::numeric_limits<float>::quiet_NaN()); check(scroll.offset() == 30);
        scroll.followLatest(); check(scroll.offset() == 200 && scroll.unread() == 0);
        scroll.setRows({{"c",150},{"d",150},{"e",150}},100); check(scroll.offset() == 350);
        scroll.scroll(-100); scroll.setRows({{"z",150}},100,true); check(scroll.following() && scroll.offset() == 50 && scroll.unread() == 0);
        bool rejected = false;
        try { scroll.setRows({{"z",1},{"z",2}},100); } catch (...) { rejected = true; }
        check(rejected);
        scroll.setRows({},100); check(scroll.offset() == 0 && scroll.totalHeight() == 0);
        std::cout << checks << " native chat scroll/retention assertions passed\n"; return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
