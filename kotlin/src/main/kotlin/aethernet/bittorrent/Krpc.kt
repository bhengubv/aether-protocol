// SPDX-License-Identifier: MIT

package aethernet.bittorrent

/** The KRPC message kind (the "y" field). */
enum class KrpcType { QUERY, RESPONSE, ERROR }

/**
 * A KRPC message (BEP-5): a bencoded dict with "t" (transaction id), "y" (q/r/e), and
 * one of a query ("q"+"a"), response ("r"), or error ("e"). The Kotlin port of
 * `go/bittorrent/krpc.go`.
 */
class KrpcMessage(
    val transactionId: ByteArray,
    val type: KrpcType,
    val method: String? = null,       // query: the "q" method name
    val arguments: BDict? = null,     // query: "a"
    val response: BDict? = null,      // response: "r"
    val errorCode: Long = 0,          // error: e[0]
    val errorMessage: String? = null, // error: e[1]
) {
    /** Serializes the message to canonical bencode. */
    fun encode(): ByteArray {
        val d = BDict()
        d.add("t", BStr(transactionId))
        when (type) {
            KrpcType.QUERY -> {
                d.add("y", BStr("q"))
                d.add("q", BStr(method ?: ""))
                d.add("a", arguments ?: BDict())
            }
            KrpcType.RESPONSE -> {
                d.add("y", BStr("r"))
                d.add("r", response ?: BDict())
            }
            KrpcType.ERROR -> {
                d.add("y", BStr("e"))
                d.add("e", BList(listOf(BInt(errorCode), BStr(errorMessage ?: ""))))
            }
        }
        return Bencode.encode(d)
    }

    companion object {
        /** Parses a KRPC message. */
        fun decode(data: ByteArray): KrpcMessage {
            val d = Bencode.decode(data).asDict()

            val transactionId = (d.get("t") ?: throw BencodeException("KRPC message has no 't'")).asBytes()
            val y = (d.get("y") ?: throw BencodeException("KRPC message has no 'y'")).asText()

            return when (y) {
                "q" -> {
                    val method = (d.get("q") ?: throw BencodeException("KRPC query has no 'q'")).asText()
                    val arguments = (d.get("a") as? BDict)
                    KrpcMessage(transactionId, KrpcType.QUERY, method = method, arguments = arguments)
                }
                "r" -> KrpcMessage(transactionId, KrpcType.RESPONSE, response = d.get("r") as? BDict)
                "e" -> {
                    var code = 0L
                    var message = ""
                    (d.get("e") as? BList)?.let { list ->
                        if (list.items.size >= 2) {
                            code = list.items[0].asInt()
                            message = list.items[1].asText()
                        }
                    }
                    KrpcMessage(transactionId, KrpcType.ERROR, errorCode = code, errorMessage = message)
                }
                else -> throw BencodeException("unknown KRPC y=\"$y\"")
            }
        }
    }
}
