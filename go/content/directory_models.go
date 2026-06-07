// SPDX-License-Identifier: MIT

package content

import (
	"time"

	"github.com/google/uuid"
)

// NamePublishPayload is the wire payload for protocol.NamePublish. Serialized
// as JSON with snake_case property names for cross-language interop.
//
// Two modes:
//   - Unsolicited broadcast: the publisher emits this on DirectoryService.Publish.
//     InResponseToQueryID is nil.
//   - Query response: a peer that holds the name emits this in unicast back to
//     a querier. InResponseToQueryID carries the query's correlation id.
//
// Field shape MUST match the C# AetherNet.Content.Models.NamePublishPayload
// for cross-language byte equality.
type NamePublishPayload struct {
	// Name is the application-layer name being announced.
	Name string `json:"name"`

	// Descriptor is the full descriptor that the name resolves to.
	Descriptor ContentDescriptor `json:"descriptor"`

	// InResponseToQueryID, if non-nil, marks this as a unicast response to a
	// prior NameQuery whose QueryID matches this value. If nil, the publish
	// is unsolicited.
	//
	// Serialized as either a UUID string or JSON null — matches the C# wire
	// behaviour where Guid? null serialises to JSON null.
	InResponseToQueryID *uuid.UUID `json:"in_response_to_query_id"`
}

// NameQueryPayload is the wire payload for protocol.NameQuery. A broadcast
// request asking peers to send a NamePublishPayload for the named entry back
// to the sender, correlated by QueryID.
//
// Serialized as JSON with snake_case property names for cross-language interop.
type NameQueryPayload struct {
	// Name is the application-layer name being queried.
	Name string `json:"name"`

	// QueryID is the correlation id. Echoed by responders in
	// NamePublishPayload.InResponseToQueryID so the querier can match
	// responses to outstanding queries.
	QueryID uuid.UUID `json:"query_id"`
}

// DirectoryEntryAnnouncedEvent is the payload delivered to
// DirectoryService.OnEntryAnnounced when a NamePublish packet arrives and the
// local catalogue learns a new (or replaced) name -> descriptor binding.
type DirectoryEntryAnnouncedEvent struct {
	// Name is the newly-learned application-layer name.
	Name string

	// Descriptor is the descriptor the name resolves to.
	Descriptor ContentDescriptor

	// SourceUhid is the UHID of the peer that emitted the announcement.
	SourceUhid string

	// AnnouncedAtUtc is the UTC time the announcement arrived locally.
	AnnouncedAtUtc time.Time
}
