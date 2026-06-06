# Aether Mesh Protocol - Implementação em TypeScript

[English](../../../../typescript/README.md) · [Français](../../fr/typescript/README.md) · [Español](../../es/typescript/README.md) · [العربية](../../ar/typescript/README.md) · [中文简体](../../zh-CN/typescript/README.md) · [日本語](../../ja/typescript/README.md) · [Deutsch](../../de/typescript/README.md) · [Português (BR)](README.md) · [Русский](../../ru/typescript/README.md) · [فارسی](../../fa/typescript/README.md) · [한국어](../../ko/typescript/README.md)

Uma implementação completa em TypeScript/Node.js do protocolo de rede mesh Aether, totalmente compatível com o formato de transmissão da implementação de referência em C#.

## Funcionalidades

- **Serialização de MeshPacket**: Formato binário de transmissão idêntico ao C# (inteiros little-endian, strings/arrays prefixados por comprimento)
- **Assinatura Ed25519**: Usando TweetNaCl para geração e verificação de assinaturas
- **Signal Protocol**: Troca de chaves X3DH com derivação de chaves HKDF-SHA256 e criptografia AES-256-GCM
- **Assinatura de Pacotes**: Construção completa de dados assináveis conforme a especificação do protocolo (Seção 2.3)
- **Transporte em Processo**: Rede simulada para testes e demonstrações
- **Ratchet Simétrico**: Avanço da chave de cadeia HMAC-SHA256 com suporte a mensagens fora de ordem
- **Constantes do Protocolo**: Mais de 60 constantes da Seção A da PROTOCOL_SPEC

## Instalação

```bash
npm install
```

## Uso

### Build

```bash
npm run build
```

### Executar Demonstração

```bash
npm run dev
```

A demonstração:
1. Cria 2 nós em uma rede simulada em processo
2. Gera pares de chaves Ed25519
3. Estabelece sessões do protocolo Signal
4. Cria, assina e verifica um pacote
5. Serializa e desserializa pacotes
6. Criptografa e descriptografa mensagens
7. Envia pacotes pela camada de transporte

### Exemplos de API

#### Criação e Assinatura de Pacotes

```typescript
import { MeshPacket, PacketType, signPacket, Ed25519Service } from '@bhengubv/aether-protocol';

// Create packet
const packet = MeshPacket.create(PacketType.Data, "node-a");
packet.destinationUhid = "node-b";
packet.payload = new TextEncoder().encode("Hello");

// Sign it
const keyPair = Ed25519Service.generateKeyPair();
signPacket(packet, keyPair.privateKey);

// Verify
const isValid = verifyPacket(packet, keyPair.publicKey);
```

#### Criptografia com Signal Protocol

```typescript
import { SignalProtocol } from '@bhengubv/aether-protocol';

const signal = new SignalProtocol();

// Generate pre-key bundle
const bundle = await signal.generatePreKeyBundle("my-uhid");

// Process peer's bundle to establish session
await signal.processPreKeyBundle(peerBundle);

// Encrypt message
const encrypted = await signal.encrypt("peer-uhid", plaintext);

// Decrypt message
const decrypted = await signal.decrypt("peer-uhid", encrypted);
```

#### Serialização de Pacotes

```typescript
import { PacketSerializer } from '@bhengubv/aether-protocol';

// Serialize to binary
const binary = PacketSerializer.serialize(packet);

// Deserialize from binary
const restored = PacketSerializer.deserialize(binary);
```

#### Transporte em Processo

```typescript
import { InProcessTransport } from '@bhengubv/aether-protocol';

const nodeA = new InProcessTransport("uhid-a");
const nodeB = new InProcessTransport("uhid-b");

// Listen for incoming data
nodeB.onDataReceived = (sender, data) => {
  console.log(`Received ${data.length} bytes from ${sender}`);
};

// Send data
await nodeA.sendAsync("uhid-b", payload);
```

## Conformidade com o Protocolo

### Formato de Transmissão

Todos os inteiros de múltiplos bytes são **little-endian**:
- ID do pacote: UUID de 16 bytes
- TTL, TimestampMs: int32/int64 LE
- Comprimentos de strings: uint16 LE (não uint32)
- Comprimento do payload: int32 LE

### Assinatura de Pacotes (Seção 2.3)

Formato dos dados assináveis:
```
PacketNonce (8 bytes)
|| TimestampMs (8 bytes, LE int64)
|| Type (4 bytes, LE int32)
|| SourceUhidLength (4 bytes, LE int32)
|| SourceUhid (UTF-8)
|| DestinationUhidLength (4 bytes, LE int32)
|| DestinationUhid (UTF-8)
|| SHA-256(Payload) (32 bytes)
|| Ttl (4 bytes, LE int32)
|| Priority (4 bytes, LE int32)
```

### Signal Protocol (Seção 4)

- **Troca de Chaves**: X3DH com ECDH P-256
- **HKDF**: SHA256 com salt="AetherNetSignal"
- **Strings de Info**: "aether-root-v1", "aether-chain-send-v1", "aether-chain-recv-v1"
- **Criptografia**: AES-256-GCM com nonce de 12 bytes, tag de 16 bytes
- **Ratchet de Cadeia**: HMAC-SHA256 com avanço por contador

## Tipos de Pacote

Todos os 23 tipos de pacote definidos:
- RouteRequest (1) - Solicitação de Rota AODV
- RouteReply (2) - Resposta de Rota AODV
- Data (3) - Dados de aplicação
- Ack (4) - Confirmação de entrega
- SosBroadcast (5) - Transmissão de emergência
- ... e mais 18 (consulte a especificação do protocolo)

## Funcionalidades de Segurança

- **Assinaturas Ed25519**: Todos os pacotes assinados conforme o protocolo v2
- **AES-256-GCM**: Chaves por mensagem com nonces únicos
- **Prevenção de Replay**: Nonce aleatório de 8 bytes + validação de timestamp
- **Sigilo Futuro**: Ratchet simétrico avança as chaves de cadeia
- **Descriptografia Fora de Ordem**: Cache de chaves de mensagem ignoradas (até 1000)

## Estrutura do Projeto

```
src/
  constants.ts           - All protocol constants
  index.ts              - Main exports
  protocol/
    MeshPacket.ts       - Packet interface & factory
    PacketType.ts       - Packet type enumeration
    PacketSerializer.ts - Binary serialization
  security/
    Ed25519Service.ts   - Ed25519 signing
    SignalProtocol.ts   - Signal protocol implementation
    PacketSigning.ts    - Packet signing & deduplication
  transport/
    ITransportService.ts    - Transport interface
    InProcessTransport.ts   - In-process simulated network
  models/
    index.ts            - Core data models
  demo.ts              - Runnable demonstration
```

## Testes

A demonstração (`npm run dev`) exercita todas as principais funcionalidades:
- Criação e serialização de pacotes (ciclo completo)
- Geração de chaves Ed25519 e verificação de assinatura
- Estabelecimento de sessão do protocolo Signal
- Criptografia e descriptografia de mensagens
- Entrega via transporte em processo

Para testes unitários, estenda usando Jest ou executor de testes similar.

## Notas de Compatibilidade

- **Formato de Transmissão C#**: 100% compatível com o PacketSerializer em C#
- **Pacotes Assinados**: Protocolo versão 2 com assinaturas Ed25519
- **Derivação HKDF**: Usando @noble/hashes (implementação JavaScript pura)
- **ECDH**: Módulo crypto nativo do Node.js (curva P-256)

## Dependências

- **tweetnacl**: Assinaturas Ed25519 via TweetNaCl
- **@noble/hashes**: Derivação de chaves HKDF-SHA256
- **uuid**: Geração e análise de UUID
- **node crypto**: AES-256-GCM, HMAC-SHA256, ECDH

## Licença

MIT - Consulte o arquivo LICENSE

## Referências

- [PROTOCOL_SPEC.md](../../docs/PROTOCOL_SPEC.md)
- [Implementação em C#](../src/)
- [TweetNaCl.js](https://github.com/dchest/tweetnacl-js)
- [Noble Hashes](https://github.com/paulmillr/noble-hashes)
