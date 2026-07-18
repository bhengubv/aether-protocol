// SPDX-License-Identifier: MIT

import Foundation

/// The KRPC message kind (the "y" field).
public enum KrpcType: Equatable {
    case query
    case response
    case error
}

/// A KRPC message (BEP-5): a bencoded dict with "t" (transaction id), "y" (q/r/e),
/// and one of a query ("q"+"a"), response ("r"), or error ("e").
public struct KrpcMessage {
    public var transactionID: [UInt8]
    public var type: KrpcType
    public var method: String        // query: the "q" method name
    public var arguments: BDict?     // query: "a"
    public var response: BDict?      // response: "r"
    public var errorCode: Int64      // error: e[0]
    public var errorMessage: String  // error: e[1]

    public init(transactionID: [UInt8],
                type: KrpcType,
                method: String = "",
                arguments: BDict? = nil,
                response: BDict? = nil,
                errorCode: Int64 = 0,
                errorMessage: String = "") {
        self.transactionID = transactionID
        self.type = type
        self.method = method
        self.arguments = arguments
        self.response = response
        self.errorCode = errorCode
        self.errorMessage = errorMessage
    }

    /// Serializes the message to canonical bencode.
    public func encode() throws -> [UInt8] {
        let d = BDict()
        try d.add("t", .bytes(transactionID))
        switch type {
        case .query:
            try d.add("y", .text("q"))
            try d.add("q", .text(method))
            try d.add("a", .dict(arguments ?? BDict()))
        case .response:
            try d.add("y", .text("r"))
            try d.add("r", .dict(response ?? BDict()))
        case .error:
            try d.add("y", .text("e"))
            try d.add("e", .list([.int(errorCode), .text(errorMessage)]))
        }
        return bencodeEncode(.dict(d))
    }
}

public enum KrpcError: Error, Equatable {
    case malformed(String)
}

/// Parses a KRPC message.
public func decodeKrpc(_ data: [UInt8]) throws -> KrpcMessage {
    let v = try bencodeDecode(data)
    let d = try v.dictValue()

    guard let tVal = d.get("t") else {
        throw KrpcError.malformed("KRPC message has no 't'")
    }
    let transactionID = try tVal.bytesValue()

    guard let yVal = d.get("y") else {
        throw KrpcError.malformed("KRPC message has no 'y'")
    }
    let y = try yVal.textValue()

    switch y {
    case "q":
        guard let qVal = d.get("q") else {
            throw KrpcError.malformed("KRPC query has no 'q'")
        }
        let method = try qVal.textValue()
        var args: BDict? = nil
        if let aVal = d.get("a") { args = try? aVal.dictValue() }
        return KrpcMessage(transactionID: transactionID, type: .query, method: method, arguments: args)
    case "r":
        var resp: BDict? = nil
        if let rVal = d.get("r") { resp = try? rVal.dictValue() }
        return KrpcMessage(transactionID: transactionID, type: .response, response: resp)
    case "e":
        var code: Int64 = 0
        var message = ""
        if let eVal = d.get("e"), let list = try? eVal.listValue(), list.count >= 2 {
            code = (try? list[0].intValue()) ?? 0
            message = (try? list[1].textValue()) ?? ""
        }
        return KrpcMessage(transactionID: transactionID, type: .error, errorCode: code, errorMessage: message)
    default:
        throw KrpcError.malformed("unknown KRPC y=\(y)")
    }
}
