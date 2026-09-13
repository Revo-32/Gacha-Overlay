#include "core_connection.hpp"

namespace core {
bool CoreSession::synchronize(std::optional<SnapshotDocument>& current, std::stop_token stop) {
    socket_.send(coreHello(current ? &*current : nullptr));
    for (unsigned frames = 0; frames < 130; ++frames) if (receiveUpdate(current,stop)) return lastResumed_;
    throw std::runtime_error("Core ready not received within transaction bounds");
}
bool CoreSession::receiveUpdate(std::optional<SnapshotDocument>& current, std::stop_token stop) {
    const auto result = state_.accept(socket_.receive(stop),current);
    if (result == CoreFrameResult::heartbeat) socket_.send("{\"protocolVersion\":1,\"type\":\"heartbeat_ack\"}");
    lastResumed_ = result == CoreFrameResult::resumed;
    return result == CoreFrameResult::ready || lastResumed_;
}
CoreFrameResult CoreStreamState::accept(std::string_view text, std::optional<SnapshotDocument>& current, Clock::time_point now) {
    // Heartbeats cannot keep an incomplete bootstrap/ready transaction alive forever.
    if (transactionStarted_ && now-*transactionStarted_ >= std::chrono::seconds(20)) throw std::runtime_error("Core transaction deadline exceeded");
    Json json(text,maxFrameBytes);
    auto* root = json.root();
    if (numberField(root,"protocolVersion") != 1) throw std::runtime_error("Core protocol version mismatch");
    const auto type = textField(root,"type");
    if (type == "heartbeat") return CoreFrameResult::heartbeat;
    if (type == "core_snapshot_chunk_v1") {
        if (pending_) throw std::runtime_error("Duplicate complete snapshot before ready");
        if (!transactionStarted_) transactionStarted_ = now;
        pending_ = assembler_.accept(text); return CoreFrameResult::pending;
    }
    if (type != "core_ready_v1") throw std::runtime_error("Unexpected Core frame");
    auto* resumedValue = field(root,"resumed");
    if (!yyjson_is_bool(resumedValue)) throw std::runtime_error("Invalid resume indicator");
    const bool resumed = yyjson_get_bool(resumedValue);
    if ((resumed && (pending_ || !current || assembler_.pendingBytes() != 0)) || (!resumed && !pending_))
        throw std::runtime_error("Invalid atomic Core ready transition");
    const auto& candidate = resumed ? *current : *pending_;
    if (textField(root,"generation") != candidate.generation || numberField(root,"revision") != candidate.revision ||
        (current && candidate.generation == current->generation && candidate.revision < current->revision))
        throw std::runtime_error("Ready identity mismatch or revision rollback");
    if (pending_) { current = std::move(pending_); pending_.reset(); }
    transactionStarted_.reset();
    return resumed ? CoreFrameResult::resumed : CoreFrameResult::ready;
}
}
