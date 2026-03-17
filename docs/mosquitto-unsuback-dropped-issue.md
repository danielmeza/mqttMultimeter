# Mosquitto Bug: UNSUBACK (and other control packets) silently dropped when outgoing queue is full

## Summary

When a client subscribes to a high-throughput wildcard topic (e.g., `#`) on a busy broker, the outgoing packet queue for that client fills up with PUBLISH packets. If the client then sends an UNSUBSCRIBE, the broker correctly processes it (stops routing new messages), but the UNSUBACK response packet is **silently dropped** by `packet__queue_append()` because the outgoing queue has reached `max_queued_messages`. The client never receives the UNSUBACK and eventually times out.

This violates the MQTT specification, which requires the broker to always respond to UNSUBSCRIBE with UNSUBACK (MQTT 3.1.1 §3.10.4, MQTT 5.0 §3.10.4).

## Root Cause

In `lib/packet_mosq.c`, `packet__queue_append()` applies the `max_queued_messages` limit to **all** packet types indiscriminately:

```c
static void packet__queue_append(struct mosquitto *mosq, struct mosquitto__packet *packet)
{
#ifdef WITH_BROKER
    if(db.config->max_queued_messages > 0 && mosq->out_packet_count >= db.config->max_queued_messages){
        mosquitto_free(packet);   // ← silently drops ANY packet type
        if(mosq->is_dropping == false){
            mosq->is_dropping = true;
            log__printf(NULL, MOSQ_LOG_NOTICE,
                    "Outgoing messages are being dropped for client %s.",
                    mosq->id);
        }
        metrics__int_inc(mosq_counter_mqtt_publish_dropped, 1);
        return;
    }
#endif
    // ... append to linked list ...
}
```

This function is called by `send__unsuback()` → `packet__queue()` → `packet__queue_append()`, as well as by `send__suback()`, `send__puback()`, `send__pubrec()`, `send__pubcomp()`, `send__pubrel()`, `send__pingresp()`, and `send__disconnect()`.

All of these control packets can be silently dropped when the queue is full. Additionally, the metric incremented (`mosq_counter_mqtt_publish_dropped`) is misleading since non-PUBLISH packets are also being dropped.

## Call Chain

```
handle__unsubscribe()           // src/handle_unsubscribe.c
  → sub__remove()               // removes subscription ✓
  → send__unsuback()            // src/send_unsuback.c
    → packet__alloc()           // allocates UNSUBACK packet
    → packet__queue()           // lib/packet_mosq.c
      → packet__queue_append()  // ← DROP happens here if queue full
```

## Steps to Reproduce

1. Start a Mosquitto broker with default `max_queued_messages` (1000) or any finite value
2. Have many clients publishing messages across various topics
3. Connect a client and subscribe to `#`
4. Wait until the client's outgoing queue fills up (client processes messages slower than they arrive)
5. Send UNSUBSCRIBE for `#`
6. Observe: the broker logs "Sending UNSUBACK to \<clientid\>" but the client never receives it, eventually timing out

### Observed Timeline

```
 0s   Client subscribes to '#'
20s   Client sends UNSUBSCRIBE
30s   Messages stop arriving (broker honored the unsubscribe)
...   (silence — UNSUBACK never arrives)
100s  Client times out waiting for UNSUBACK
```

## Expected Behavior

Control packets (UNSUBACK, SUBACK, PUBACK, PUBREC, PUBREL, PUBCOMP, PINGRESP, DISCONNECT) should **never** be subject to `max_queued_messages` limits. Only PUBLISH packets should be dropped when the queue is full. The MQTT specification mandates that the broker must send acknowledgement packets for protocol correctness.

## Suggested Fix

In `packet__queue_append()`, check the packet command type before applying the drop logic. Only drop PUBLISH packets (`CMD_PUBLISH`):

```c
static void packet__queue_append(struct mosquitto *mosq, struct mosquitto__packet *packet)
{
#ifdef WITH_BROKER
    if(db.config->max_queued_messages > 0
        && mosq->out_packet_count >= db.config->max_queued_messages
        && (packet->command & 0xF0) == CMD_PUBLISH){

        mosquitto_free(packet);
        if(mosq->is_dropping == false){
            mosq->is_dropping = true;
            log__printf(NULL, MOSQ_LOG_NOTICE,
                    "Outgoing messages are being dropped for client %s.",
                    mosq->id);
        }
        metrics__int_inc(mosq_counter_mqtt_publish_dropped, 1);
        return;
    }
#endif
    // ... append to linked list ...
}
```

## Environment

- Mosquitto version: tested against `test.mosquitto.org` (public broker), confirmed by source code review of current `main` branch
- Client: MQTTnet v5.1.0 (.NET)
- Protocol: MQTT v5.0 and v3.1.1 (both affected)

## Impact

- Clients cannot reliably unsubscribe from high-throughput wildcard topics
- QoS 1/2 acknowledgement packets (PUBACK, PUBREC, etc.) may also be silently dropped, causing message redelivery storms
- PINGRESP may be dropped, causing the client to believe the connection is dead
- The `mosq_counter_mqtt_publish_dropped` metric is misleading since it counts dropped non-PUBLISH packets too

## MQTT Specification References

All the following are MUST-level requirements from the MQTT 5.0 OASIS Standard:

- **[§3.4.4 PUBACK Actions](https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html#_Toc3901126)**: "The Server MUST send a PUBACK packet containing the Packet Identifier from the incoming PUBLISH packet, having accepted ownership of the Application Message"
- **[§3.5.4 PUBREC Actions](https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html#_Toc3901134)**: "The receiver MUST respond with either a PUBREC [...] having accepted ownership of the Application Message"
- **[§3.7.4 PUBCOMP Actions](https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html#_Toc3901152)**: "The receiver MUST respond to a PUBREL packet by sending a PUBCOMP packet"
- **[§3.9.4 SUBACK Actions](https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html#_Toc3901172)**: "The Server MUST send a SUBACK Packet in response to a SUBSCRIBE Packet"
- **[§3.10.4 UNSUBACK Actions](https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html#_Toc3901182)**: "The Server MUST respond to an UNSUBSCRIBE request by sending an UNSUBACK Packet"
- **[§3.12.4 PINGRESP](https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html#_Toc3901200)**: "The Server MUST send a PINGRESP packet in response to a PINGREQ packet"
