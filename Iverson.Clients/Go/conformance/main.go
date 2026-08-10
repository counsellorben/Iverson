// The Go conformance driver.
//
// Mirrors the Python driver's shape (Iverson.Clients/Python/conformance/driver.py): reports,
// never asserts. Every step's failure is data — ok:false with an error message — and the process
// still exits 0. A non-zero exit means the driver itself broke (bad flags, unsupported scenario,
// unwritable --out).
//
// Invoked as `bin/conformance <flags>` with cwd Iverson.Clients/Go, after
// `go build -o bin/conformance ./conformance` (DriverRunner.cs:105-107).
package main

import (
	"context"
	"crypto/md5"
	"encoding/json"
	"fmt"
	"os"
	"strings"

	"google.golang.org/grpc"
	"google.golang.org/protobuf/encoding/protojson"

	"github.com/iverson/clients/go/iverson"

	pb "github.com/iverson/clients/go/generated"
)

const (
	language = "go"
	scenario = "crud-roundtrip"
)

// ── Argument parsing ──────────────────────────────────────────────────────────

// args is a minimal `--flag value` parser, mirroring the Python driver's Args.
type args struct {
	values map[string]string
}

func parseArgs(argv []string) args {
	values := make(map[string]string)
	i := 0
	for i < len(argv) {
		flag := argv[i]
		if !strings.HasPrefix(flag, "--") {
			i++
			continue
		}
		if i+1 < len(argv) && !strings.HasPrefix(argv[i+1], "--") {
			values[flag] = argv[i+1]
			i += 2
		} else {
			values[flag] = ""
			i++
		}
	}
	return args{values: values}
}

func (a args) require(flag string) (string, error) {
	v, ok := a.values[flag]
	if !ok || v == "" {
		return "", fmt.Errorf("missing required flag %s", flag)
	}
	return v, nil
}

func (a args) optional(flag string) string {
	return a.values[flag]
}

// ── Step result / phase document ─────────────────────────────────────────────

// stepResult is one step's outcome within a phase document. All fields are always present (null
// where absent) via explicit json tags, matching the .NET driver's JsonSerializerDefaults.Web
// output, which does not omit nulls.
type stepResult struct {
	Name           string            `json:"name"`
	Ok             bool              `json:"ok"`
	Error          *string           `json:"error"`
	TypeDescriptor json.RawMessage   `json:"typeDescriptor"`
	Keys           map[string]string `json:"keys"`
	Entity         json.RawMessage   `json:"entity"`
}

type phaseDocument struct {
	Language string       `json:"language"`
	Phase    string       `json:"phase"`
	Steps    []stepResult `json:"steps"`
}

func failStep(name string, err error) stepResult {
	msg := err.Error()
	return stepResult{Name: name, Ok: false, Error: &msg}
}

func okStep(name string) stepResult {
	return stepResult{Name: name, Ok: true}
}

func entityJSON(entity interface{}) json.RawMessage {
	b, err := json.Marshal(entity)
	if err != nil {
		return nil
	}
	return json.RawMessage(b)
}

// deriveKey returns a deterministic per-run key: distinct across runs because --id-prefix is.
// Only needs to be consistent within this driver's own fallback path — cross-language key
// equality is not required, since --keys is language-qualified (each language reads only its own
// slice).
func deriveKey(idPrefix, logicalName string) string {
	sum := md5.Sum([]byte(idPrefix + ":" + logicalName))
	return formatUUID(sum)
}

func formatUUID(b [16]byte) string {
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

func parseKeys(keysJSON, lang string) map[string]string {
	out := map[string]string{}
	if keysJSON == "" {
		return out
	}
	var byLanguage map[string]map[string]string
	if err := json.Unmarshal([]byte(keysJSON), &byLanguage); err != nil {
		return out
	}
	if m, ok := byLanguage[lang]; ok {
		return m
	}
	return out
}

// ── Descriptor capture ────────────────────────────────────────────────────────

type capturedType struct {
	name string
	json string
}

// capturingMappingClient wraps the real ObjectMappingService stub — the sanctioned capture seam
// per the plan: NewSchemaRegistrar(client MappingClient, ...) takes the client as a public
// constructor parameter, and MappingClient is a one-method interface (iverson/registrar.go:12-16)
// that the generated stub's variadic-opts method does not itself satisfy. Records the outgoing
// SchemaRequest.RootType of every registration call (before forwarding, so it is captured even if
// the RPC itself fails) and forwards unchanged. Nothing is judged here — the JSON is reported
// verbatim.
type capturingMappingClient struct {
	real     pb.ObjectMappingServiceClient
	captured []capturedType
}

func (c *capturingMappingClient) RegisterSchema(ctx context.Context, req *pb.SchemaRequest) (*pb.SchemaResponse, error) {
	if req.RootType != nil {
		js, err := protojson.Marshal(req.RootType)
		if err == nil {
			c.captured = append(c.captured, capturedType{name: req.RootType.TypeName, json: string(js)})
		}
	}
	return c.real.RegisterSchema(ctx, req)
}

// selectDescriptor returns the descriptor for the first of preferredTypeNames actually sent
// under that exact name, or nil if none of them was. Never substitutes a different type's
// descriptor.
func (c *capturingMappingClient) selectDescriptor(preferredTypeNames ...string) json.RawMessage {
	for _, preferred := range preferredTypeNames {
		if preferred == "" {
			continue
		}
		for _, ct := range c.captured {
			if strings.EqualFold(ct.name, preferred) {
				return json.RawMessage(ct.json)
			}
		}
	}
	return nil
}

// ── Main ───────────────────────────────────────────────────────────────────────

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(argv []string) int {
	a := parseArgs(argv)

	sc, err := a.require("--scenario")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	if sc != scenario {
		fmt.Fprintf(os.Stderr, "unsupported scenario %q; this driver implements only %q\n", sc, scenario)
		return 2
	}

	phase, err := a.require("--phase")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	tenant, err := a.require("--tenant")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	ownerID, err := a.require("--owner-id")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	idPrefix, err := a.require("--id-prefix")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	outPath, err := a.require("--out")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	grpcAddr, err := a.require("--grpc")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	typeHint := a.optional("--type")

	clientID := a.optional("--client-id")
	clientSecret := a.optional("--client-secret")
	tokenEndpoint := a.optional("--token-endpoint")
	actingToken := a.optional("--acting-token")

	var creds *iverson.OAuth2ClientCredentials
	dialOpts := []grpc.DialOption{grpc.WithInsecure()} //nolint:staticcheck
	if clientID != "" && clientSecret != "" && tokenEndpoint != "" {
		creds = &iverson.OAuth2ClientCredentials{
			ClientID:      clientID,
			ClientSecret:  clientSecret,
			TokenEndpoint: tokenEndpoint,
		}
		dialOpts = append(dialOpts, grpc.WithPerRPCCredentials(creds))
	}

	client, err := iverson.NewIversonClient(grpcAddr, dialOpts...)
	if err != nil {
		fmt.Fprintf(os.Stderr, "connecting to %s: %v\n", grpcAddr, err)
		return 2
	}
	defer client.Close()

	ctx := context.Background()
	if actingToken != "" {
		ctx = iverson.WithActingUserToken(ctx, actingToken)
	}

	priorKeys := parseKeys(a.optional("--keys"), language)
	keyFor := func(logicalName string) string {
		if existing, ok := priorKeys[logicalName]; ok && existing != "" {
			return existing
		}
		return deriveKey(idPrefix, logicalName)
	}

	var steps []stepResult

	switch phase {
	case "register":
		capture := &capturingMappingClient{real: client.MappingStub}
		registrar := iverson.NewSchemaRegistrar(capture, GoArticle{}, GoAuthor{}, GoTag{})
		regErr := registrar.RegisterAll(ctx, idPrefix)

		addRegisterStep := func(name string, descriptor json.RawMessage) {
			step := stepResult{Name: name, Ok: regErr == nil, TypeDescriptor: descriptor}
			if regErr != nil {
				msg := regErr.Error()
				step.Error = &msg
			}
			steps = append(steps, step)
		}

		addRegisterStep("register", capture.selectDescriptor(typeHint, "GoArticle"))
		addRegisterStep("register_author", capture.selectDescriptor("GoAuthor"))
		addRegisterStep("register_tag", capture.selectDescriptor("GoTag"))

	case "write":
		authorKey := deriveKey(idPrefix, "author")
		tagKey := deriveKey(idPrefix, "tag")
		articleKey := deriveKey(idPrefix, "article")

		authorCoord, authorCoordErr := iverson.NewEntityCoordinator(client, GoAuthor{})
		tagCoord, tagCoordErr := iverson.NewEntityCoordinator(client, GoTag{})
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})

		// One step per row: a denied or failed write must not abort the others, and each row's
		// key is reported unconditionally so later phases can address the row even when the
		// write that produced it failed.
		writeStep := func(name, keyName, keyValue string, do func() (interface{}, error)) stepResult {
			var step stepResult
			entity, err := do()
			if err != nil {
				step = failStep(name, err)
			} else {
				step = okStep(name)
				step.Entity = entityJSON(entity)
			}
			step.Keys = map[string]string{keyName: keyValue}
			return step
		}

		steps = append(steps, writeStep("write_author", "author", authorKey, func() (interface{}, error) {
			if authorCoordErr != nil {
				return nil, authorCoordErr
			}
			entity := GoAuthor{Id: authorKey, TenantId: tenant, OwnerId: ownerID, Name: "author-" + idPrefix}
			_, err := authorCoord.Persist(ctx, entity)
			return entity, err
		}))

		steps = append(steps, writeStep("write_tag", "tag", tagKey, func() (interface{}, error) {
			if tagCoordErr != nil {
				return nil, tagCoordErr
			}
			entity := GoTag{Id: tagKey, TenantId: tenant, OwnerId: ownerID, Label: "tag-" + idPrefix}
			_, err := tagCoord.Persist(ctx, entity)
			return entity, err
		}))

		steps = append(steps, writeStep("write_article", "article", articleKey, func() (interface{}, error) {
			if articleCoordErr != nil {
				return nil, articleCoordErr
			}
			entity := GoArticle{
				Id: articleKey, TenantId: tenant, OwnerId: ownerID, Title: "title-" + idPrefix,
				GoAuthorId: authorKey, GoTagIds: []string{tagKey},
			}
			_, err := articleCoord.Persist(ctx, entity)
			return entity, err
		}))

	case "read":
		// Two gets at depth 0 (EntityCoordinator.Get performs no relation traversal), reported
		// separately so a failure on one is not conflated with the other.
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})
		if articleCoordErr != nil {
			steps = append(steps, failStep("get", articleCoordErr))
		} else if article, err := articleCoord.Get(ctx, keyFor("article")); err != nil {
			steps = append(steps, failStep("get", err))
		} else {
			step := okStep("get")
			step.Entity = entityJSON(article)
			steps = append(steps, step)
		}

		authorCoord, authorCoordErr := iverson.NewEntityCoordinator(client, GoAuthor{})
		if authorCoordErr != nil {
			steps = append(steps, failStep("get_author", authorCoordErr))
		} else if author, err := authorCoord.Get(ctx, keyFor("author")); err != nil {
			steps = append(steps, failStep("get_author", err))
		} else {
			step := okStep("get_author")
			step.Entity = entityJSON(author)
			steps = append(steps, step)
		}

	case "update":
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})
		if articleCoordErr != nil {
			steps = append(steps, failStep("update", articleCoordErr))
			break
		}
		entity := GoArticle{
			Id: keyFor("article"), TenantId: tenant, OwnerId: ownerID, Title: "title-" + idPrefix + "-updated",
			GoAuthorId: keyFor("author"), GoTagIds: []string{keyFor("tag")},
		}
		// EntityCoordinator.Update returns no entity (unlike .NET's UpdateMappedAsync, which
		// returns the server's response entity) — the entity reported here is what the driver
		// sent, which is the only observable this API surface offers.
		if err := articleCoord.Update(ctx, entity); err != nil {
			steps = append(steps, failStep("update", err))
		} else {
			step := okStep("update")
			step.Entity = entityJSON(entity)
			steps = append(steps, step)
		}

	case "delete":
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})
		deleteKey := keyFor("article")

		if articleCoordErr != nil {
			steps = append(steps, failStep("delete", articleCoordErr))
			steps = append(steps, failStep("get_after_delete", articleCoordErr))
			break
		}

		if err := articleCoord.Delete(ctx, deleteKey); err != nil {
			steps = append(steps, failStep("delete", err))
		} else {
			steps = append(steps, okStep("delete"))
		}

		// The read-back is its own step, carrying entity (null when nothing came back) and the
		// client's own error text when the read itself fails — a null entity alone cannot
		// distinguish "gone" from "read denied" from a transport error.
		if after, err := articleCoord.Get(ctx, deleteKey); err != nil {
			steps = append(steps, failStep("get_after_delete", err))
		} else {
			step := okStep("get_after_delete")
			step.Entity = entityJSON(after)
			steps = append(steps, step)
		}

	default:
		fmt.Fprintf(os.Stderr, "unknown phase %q\n", phase)
		return 2
	}

	document := phaseDocument{Language: language, Phase: phase, Steps: steps}
	out, err := json.Marshal(document)
	if err != nil {
		fmt.Fprintf(os.Stderr, "marshaling phase document: %v\n", err)
		return 2
	}
	if err := os.WriteFile(outPath, out, 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "writing %s: %v\n", outPath, err)
		return 2
	}

	return 0
}
