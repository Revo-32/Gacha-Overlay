#include "sales_actions.hpp"
#include <iostream>

int main() {
    unsigned assertions=0;
    const auto check=[&](bool condition){++assertions;if(!condition)throw std::runtime_error("Sales command assertion "+std::to_string(assertions));};
    const auto snapshot=[](bool completed,std::string generation="sales"){return core::Json("{\"sales\":{\"currentMessageId\":\"123\",\"actions\":{\"generation\":"+core::jsonQuote(generation)+",\"targets\":[{\"messageId\":\"123\",\"canComplete\":"+(completed?"false":"true")+",\"canUndo\":"+(completed?"true":"false")+",\"botCompleted\":"+(completed?"true":"false")+"}]}}}");};
    const auto response=[](const core::SalesCommand& command,std::string_view disposition){return "{\"protocolVersion\":1,\"clientRequestId\":"+core::jsonQuote(command.requestId)+",\"disposition\":"+core::jsonQuote(disposition)+",\"awaitingOfficialReadBack\":true}";};
    try {
        core::SalesActions actions;check(!actions.offered());actions.observe(snapshot(false));auto offer=*actions.offered();check(!offer.undo);
        check(actions.submit(offer));check(actions.busy());check(!actions.submit(offer));check(!actions.offered());
        auto command=*actions.wait({});check(command.requestId.size()==36);
        check(command.requestId.find_first_of("ABCDEF")==std::string::npos);
        core::Json body(core::salesCommandBody(command));check(core::numberField(body.root(),"messageId")==123);check(core::textField(body.root(),"desiredStatus")=="completed");
        actions.response(command,200,response(command,"accepted"));check(actions.busy()); // HTTP alone must not complete the operation.
        actions.observe(snapshot(false));check(actions.busy());actions.observe(snapshot(true));check(!actions.busy());check(actions.offered()->undo);
        check(actions.submit(*actions.offered()));command=*actions.wait({});core::Json undo(core::salesCommandBody(command));check(core::textField(undo.root(),"desiredStatus")=="clear");
        actions.observe(snapshot(false));check(actions.busy()); // Read-back can beat the response.
        actions.response(command,200,response(command,"accepted"));check(!actions.busy());
        offer=*actions.offered();actions.observe(snapshot(false,"new"));check(!actions.submit(offer));
        check(actions.submit(*actions.offered()));command=*actions.wait({});actions.uncertain(command);check(actions.busy());check(!actions.offered());
        actions.disconnected();actions.observe(snapshot(true,"newer"));check(!actions.busy()); // Lost response, recovered authoritative state.
        check(actions.submit(*actions.offered()));command=*actions.wait({});actions.response(command,200,response(command,"rejectedNotOwner"));check(!actions.busy());
        check(actions.submit(*actions.offered()));actions.disconnected();check(!actions.busy());check(!actions.offered()); // Unsent intent cancelled.
        actions.observe(core::Json("{\"sales\":{}}"));check(!actions.offered());
        actions.observe(snapshot(false));check(actions.submit(*actions.offered()));command=*actions.wait({});
        auto wrong=command;wrong.requestId="00000000-0000-0000-0000-000000000000";
        bool rejected=false;try {actions.response(command,200,response(wrong,"accepted"));}catch(const std::runtime_error&){rejected=true;}check(rejected);
        actions.uncertain(command);check(actions.busy());actions.observe(snapshot(true));check(!actions.busy());
        std::stop_source stop;stop.request_stop();check(!actions.wait(stop.get_token()));
        std::cout<<"PASS "<<assertions<<" assertions; synthetic, network/Discord writes=0\n";return 0;
    } catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
}
