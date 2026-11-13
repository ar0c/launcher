/*
 * SPDX-License-Identifier: MIT
 */

#include "../src/cp0/cp0_bluez_dbus_client.hpp"

#include <gio/gio.h>

#include <atomic>
#include <cassert>
#include <chrono>
#include <future>
#include <thread>
#include <utility>

namespace {
using namespace std::chrono_literals;
constexpr char kAddress[] = "AA:BB:CC:DD:EE:FF";
constexpr char kPath[] = "/org/bluez/hci0/dev_AA_BB_CC_DD_EE_FF";

GVariant *mock_call(GDBusConnection *bus, const char *method, GVariant *arguments = nullptr)
{
    GError *error = nullptr;
    GVariant *reply = g_dbus_connection_call_sync(
        bus, "org.bluez", kPath, "org.bluez.MockTest", method, arguments, nullptr,
        G_DBUS_CALL_FLAGS_NONE, 5000, nullptr, &error);
    if (error)
        g_error("%s: %s", method, error->message);
    return reply;
}

unsigned connect_count(GDBusConnection *bus)
{
    GVariant *reply = mock_call(bus, "ConnectCount");
    guint32 count = 0;
    g_variant_get(reply, "(u)", &count);
    g_variant_unref(reply);
    return count;
}

void wait_for_device(bool paired, bool connected)
{
    const auto deadline = std::chrono::steady_clock::now() + 3s;
    do {
        cp0_bt_device_t devices[CP0_BT_DEVICE_MAX]{};
        const int count = cp0_bluez_dbus::list(devices, CP0_BT_DEVICE_MAX, false);
        for (int i = 0; i < count; ++i) {
            const auto &device = devices[i];
            if (device.address == std::string(kAddress) &&
                device.paired == paired && device.connected == connected) {
                assert(!device.trusted);
                return;
            }
        }
        std::this_thread::sleep_for(10ms);
    } while (std::chrono::steady_clock::now() < deadline);
    g_error("Device state did not reach paired=%d connected=%d", paired, connected);
}

void configure(GDBusConnection *bus, const char *behavior, bool paired)
{
    g_variant_unref(mock_call(bus, "ConfigureConnect", g_variant_new("(sb)", behavior, paired)));
    wait_for_device(paired, false);
}

using Result = std::pair<int, std::string>;

std::future<Result> connect(const char *address = kAddress)
{
    auto promise = std::make_shared<std::promise<Result>>();
    cp0_bluez_dbus::connect_async(address, [promise](int code, const std::string &message) {
        promise->set_value({code, message});
    });
    return promise->get_future();
}

Result complete(std::future<Result> result)
{
    assert(result.wait_for(8s) == std::future_status::ready);
    return result.get();
}
} // namespace

int main()
{
    GDBusConnection *bus = g_bus_get_sync(G_BUS_TYPE_SYSTEM, nullptr, nullptr);
    assert(bus);
    cp0_bluez_dbus::initialize();
    std::atomic<unsigned> prompts{0};
    cp0_bluez_dbus::set_agent_listener([&prompts](const auto &request) {
        ++prompts;
        cp0_bluez_dbus::agent_reply(request.id, true, {});
    });

    configure(bus, "fail", true);
    for (int i = 0; i < 3; ++i)
        g_variant_unref(mock_call(bus, "AuthorizeService", g_variant_new("(b)", i == 0)));
    auto joining_reconnect = connect();
    assert(prompts == 0);
    assert(complete(std::move(joining_reconnect)).first == 0);
    assert(connect_count(bus) == 0);
    wait_for_device(true, true);
    assert(complete(connect()).first == 0);
    assert(connect_count(bus) == 0);

    configure(bus, "slow", true);
    auto first = connect();
    auto duplicate = connect("aa:bb:cc:dd:ee:ff");
    assert(complete(std::move(first)).first == 0);
    assert(complete(std::move(duplicate)).first == 0);
    assert(connect_count(bus) == 1);

    configure(bus, "race", true);
    assert(complete(connect()).first == 0);
    assert(connect_count(bus) == 1);

    configure(bus, "busy", true);
    assert(complete(connect()).first == 0);
    assert(connect_count(bus) == 1);

    configure(bus, "fail", true);
    const auto failure = complete(connect());
    assert(failure.first < 0 && failure.second.find("br-connection-unknown") != std::string::npos);
    assert(connect_count(bus) == 1);

    configure(bus, "slow", false);
    g_variant_unref(mock_call(bus, "AuthorizeService", g_variant_new("(b)", false)));
    assert(prompts == 1);

    auto pairing = std::make_shared<std::promise<Result>>();
    cp0_bluez_dbus::pair_async(kAddress, [pairing](int code, const std::string &message) {
        pairing->set_value({code, message});
    });
    assert(complete(pairing->get_future()).first == 0);
    assert(prompts == 2);

    cp0_bluez_dbus::shutdown();
    g_object_unref(bus);
}
