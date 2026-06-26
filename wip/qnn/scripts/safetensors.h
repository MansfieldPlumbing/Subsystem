#pragma once
#include <string>
#include <vector>
#include <fstream>
#include <iostream>
#include <unordered_map>

struct TensorInfo {
    std::string dtype;
    std::vector<long long> shape;
    long long start;
    long long end;
};

static std::unordered_map<std::string, TensorInfo>
ParseSafetensorsHeader(const char* path, uint64_t& header_len_out) {
    std::unordered_map<std::string, TensorInfo> tensors;
    std::ifstream file(path, std::ios::binary);

    uint64_t json_len = 0;
    file.read((char*)&json_len, sizeof(uint64_t));
    header_len_out = json_len;

    std::string json_str(json_len, '\0');
    file.read(&json_str[0], json_len);

    size_t pos = 0;
    while ((pos = json_str.find("\"", pos)) != std::string::npos) {
        size_t name_start = pos + 1;
        size_t name_end = json_str.find("\"", name_start);
        std::string name = json_str.substr(name_start, name_end - name_start);
        pos = name_end + 1;

        if (json_str.substr(pos, 2) != ":{") continue;

        TensorInfo info;
        size_t offsets_pos = json_str.find("\"data_offsets\":[", name_end);
        if (offsets_pos != std::string::npos) {
            sscanf_s(json_str.c_str() + offsets_pos, "\"data_offsets\":[%lld,%lld]", &info.start, &info.end);
            tensors[name] = info;
        }
    }
    return tensors;
}
