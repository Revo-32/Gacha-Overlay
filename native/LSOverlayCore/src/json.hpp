#pragma once
#include <yyjson.h>
#include <windows.h>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_set>

namespace core {
inline void validateJsonTree(yyjson_val* value, unsigned depth = 0) {
    if (depth > 32) throw std::runtime_error("JSON nesting limit exceeded");
    if (yyjson_is_obj(value)) {
        std::unordered_set<std::string_view> names;
        std::size_t index = 0, count = 0;
        yyjson_val* key = nullptr; yyjson_val* member = nullptr;
        yyjson_obj_foreach(value,index,count,key,member) {
            if (!names.emplace(yyjson_get_str(key),yyjson_get_len(key)).second)
                throw std::runtime_error("Duplicate JSON member rejected");
            validateJsonTree(member,depth+1);
        }
    } else if (yyjson_is_arr(value)) {
        std::size_t index = 0, count = 0; yyjson_val* member = nullptr;
        yyjson_arr_foreach(value,index,count,member) validateJsonTree(member,depth+1);
    }
}
// Auth JSON can contain secrets. Wipe owned decoded strings before releasing it.
inline void wipeJson(yyjson_val* value, unsigned depth = 0) noexcept {
    if (depth > 32) return; // Bound cleanup recursion even for rejected documents.
    if (yyjson_is_str(value)) SecureZeroMemory(const_cast<char*>(yyjson_get_str(value)),yyjson_get_len(value));
    else if (yyjson_is_obj(value)) {
        std::size_t index = 0, count = 0; yyjson_val* key = nullptr; yyjson_val* member = nullptr;
        yyjson_obj_foreach(value,index,count,key,member) { wipeJson(key,depth+1); wipeJson(member,depth+1); }
    } else if (yyjson_is_arr(value)) {
        std::size_t index = 0, count = 0; yyjson_val* member = nullptr;
        yyjson_arr_foreach(value,index,count,member) wipeJson(member,depth+1);
    }
}
struct JsonDeleter { void operator()(yyjson_doc* doc) const noexcept { if (doc) { wipeJson(yyjson_doc_get_root(doc)); yyjson_doc_free(doc); } } };
class Json final {
public:
    explicit Json(std::string_view input, std::size_t limit = 1024 * 1024) {
        if (input.empty() || input.size() > limit) throw std::runtime_error("JSON byte limit exceeded");
        doc_.reset(yyjson_read(input.data(),input.size(),YYJSON_READ_NOFLAG));
        if (!doc_) throw std::runtime_error("Invalid JSON/UTF-8");
        validateJsonTree(root());
    }
    yyjson_val* root() const noexcept { return yyjson_doc_get_root(doc_.get()); }
private:
    std::unique_ptr<yyjson_doc,JsonDeleter> doc_;
};
inline yyjson_val* field(yyjson_val* object, const char* name) {
    if (!yyjson_is_obj(object)) throw std::runtime_error("Expected JSON object");
    return yyjson_obj_get(object,name);
}
inline std::string_view stringValue(yyjson_val* value) {
    if (!yyjson_is_str(value)) throw std::runtime_error("Expected JSON string");
    return {yyjson_get_str(value),yyjson_get_len(value)};
}
inline std::string_view textField(yyjson_val* object, const char* name) { return stringValue(field(object,name)); }
inline std::uint64_t number(yyjson_val* value) {
    if (!yyjson_is_uint(value) || yyjson_get_uint(value) > INT64_MAX) throw std::runtime_error("Expected bounded nonnegative JSON integer");
    return yyjson_get_uint(value);
}
inline std::uint64_t numberField(yyjson_val* object, const char* name) { return number(field(object,name)); }
inline std::string jsonQuote(std::string_view value) {
    std::unique_ptr<yyjson_mut_doc,decltype(&yyjson_mut_doc_free)> doc(yyjson_mut_doc_new(nullptr),yyjson_mut_doc_free);
    if (!doc) throw std::bad_alloc();
    yyjson_mut_doc_set_root(doc.get(),yyjson_mut_strncpy(doc.get(),value.data(),value.size()));
    std::size_t size = 0;
    std::unique_ptr<char,decltype(&free)> output(yyjson_mut_write(doc.get(),YYJSON_WRITE_NOFLAG,&size),free);
    if (!output) throw std::bad_alloc();
    return {output.get(),size};
}
}
