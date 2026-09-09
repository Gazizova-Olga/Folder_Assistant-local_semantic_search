# Implementation status

What is live, what is built but unreachable, and what is not built — as of 2026-09-09.

**Three states, not two.** "Built but unreachable" is the largest category here, and a diagram
with only *done* and *not done* hides it: retrieval is implemented, tested and correct, and it
also never runs. Colouring those the same as either neighbour would misrepresent the system in
opposite directions depending on which you chose.

```mermaid
flowchart LR
    subgraph live["Live — runs in the application"]
        direction TB
        scan["LocalTextFileScanner<br/>walk, filter, hash"]
        chunk["SimpleTokenizer + TextChunker<br/>overlapping windows"]
        embed["IVectorizer<br/>programmable · LSA"]
        store["FolderIndexRepository<br/>SQLite, WAL"]
        watch["FileSystemWatcherChangeFeed<br/>debounced, coalesced"]
        state["IndexState<br/>Building · Ready · Failed"]
    end

    subgraph built["Built but unreachable — no runtime caller"]
        direction TB
        query["CosineRetrievalQuery<br/>tested, not registered"]
    end

    subgraph notbuilt["Not built"]
        direction TB
        tools["Filesystem tools"]
        agent["Agent + provider adapter"]
        chatui["Chat surface"]
    end

    scan --> chunk --> embed --> store
    watch --> scan
    store -.-> state
    store -.->|"vectors nothing reads"| query
    query -.-> tools -.-> agent -.-> chatui

    classDef liveCls fill:#1b5e20,stroke:#a5d6a7,color:#ffffff
    classDef builtCls fill:#e65100,stroke:#ffcc80,color:#ffffff
    classDef gapCls fill:#37474f,stroke:#b0bec5,color:#ffffff

    class scan,chunk,embed,store,watch,state liveCls
    class query builtCls
    class tools,agent,chatui gapCls
```

## The gap, stated plainly

`CosineRetrievalQuery` is not registered in the composition root, and nothing outside
`Retrieval/` and the test suite references `IRetrievalQuery`. Every pass writes vectors that the
running application never reads.

The write half is complete and the query half exists. They are not connected, and the work to
connect them is a runtime caller — which in the plan means the filesystem tools and the agent
that calls them. Until then the vector store is exercised only by tests.

## What each state means here

| State | Meaning |
|---|---|
| **Live** | Constructed by the composition root and reached when the application runs. |
| **Built but unreachable** | Implemented and covered by tests; no code path in a running process reaches it. |
| **Not built** | No implementation. Specified in places, but not written. |

The middle row is the one worth watching. Code in it passes every test it has and contributes
nothing to the product, which is a state that looks like progress in a test summary and is not.
