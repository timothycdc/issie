module DeveloperModeView

open EEExtensions
open VerilogTypes
open Fulma

open Fable.React
open Fable.React.Props

open JSHelpers
open ModelType
open CommonTypes
open DrawModelType
open DrawModelType.SymbolT
open DrawModelType.BusWireT
open DiagramStyle
open BlockHelpers
open DeveloperModeHelpers
open Symbol
open Optics
open BusWireRoute
open BusWireRoutingHelpers.Constants
open BusWireRoutingHelpers
open Sheet
open DrawModelType.SheetT


/// function that returns the an string ID with extra formatting of a hovered wire, symbol, or ports
let findHoveredID (pos: XYPos) (model: SheetT.Model) =
    let dummySymbolId: ComponentId = ComponentId "dummy"
    // we add a 'dummy symbol' to the model to represent the mouse position
    // solely for calculation purposes, it will not be added to the actual model
    // for convenience, we let dummy symbol be 30x30, equal to a Not gate size
    let h, w = 30.0, 30.0
    let mouseComponentDummy =
        { Id = "dummy"
          Type = Not
          Label = "dummy"
          InputPorts = List.empty
          OutputPorts = List.empty
          X = pos.X - float w / 2.0
          Y = pos.Y - float h / 2.0
          H = float h
          W = float w
          SymbolInfo = None }

    // create a mouse dummy symbol, find its bounding box, add it to a dummy model
    let mouseSymbolDummy: Symbol =
        { (createNewSymbol [] pos NotConnected "" White) with
            Component = mouseComponentDummy }
    let boundingBoxes_ =
        Lens.create (fun m -> m.BoundingBoxes) (fun bb m -> { m with BoundingBoxes = bb })

    let dummyModel =
        model
        |> Optic.set (SheetT.symbols_) (Map.add dummySymbolId mouseSymbolDummy model.Wire.Symbol.Symbols)
        // SheetUpdateHelpers has not implemented updateBoundingBoxes yet on master
        |> Optic.set boundingBoxes_ (Symbol.getBoundingBoxes model.Wire.Symbol)
        |> Optic.map symbols_ (Map.map (fun _ sym -> Symbol.calcLabelBoundingBox sym))
    // we calculate the bounding box of the mouse
    let mouseBoundingBox = getSymbolBoundingBox mouseSymbolDummy

    // inspired by SheetBeautifyD1's findAllBoundingBoxesOfSymIntersections
    let intersectingWiresInfo =
        dummyModel.Wire.Wires
        |> Map.values
        // findWireSymbolIntersections returns a list of bounding boxes of symbols intersected by wire.
        // we find the wires that have a boundingBox in their intersection list that contains our mouseBoundingBox
        // we might get more than one wire – so get a list

        |> Seq.map (fun wire -> (wire, (findWireSymbolIntersections dummyModel.Wire wire)))
        |> Seq.choose (fun (wire, bboxes) ->
            if
                bboxes
                |> List.exists (fun box ->

                    // findWireSymbolIntersections returns bounding boxes that have been enlarged with minWireSeparation
                    // we correct this
                    let correctedBox =
                        { W = box.W - minWireSeparation * 2.
                          H = box.H - minWireSeparation * 2.
                          TopLeft =
                            box.TopLeft
                            |> updatePos Right_ minWireSeparation
                            |> updatePos Down_ minWireSeparation }
                    mouseBoundingBox =~ correctedBox)
            then
                Some(wire.WId.ToString())

            else
                None)
        |> Seq.toList
        |> List.tryHead

    // inspired by SheetBeautifyD1's findAllBoundingBoxesOfSymIntersections
    let intersectingSymbolInfo =
        model.BoundingBoxes
        |> Map.toList
        // get all boundingBoxes in model not equal to symbolBoundingBox, see if they overlap with symbolBoundingBox, if yes, return compId
        |> List.filter (fun (compId, box) -> not (box =~ mouseBoundingBox))
        |> List.choose (fun (compId, box) ->
            match (overlapArea2DBox mouseBoundingBox box) with
            | Some area -> Some(compId.ToString())
            | None -> None)
        |> List.tryHead

    // inpisred by Sheet.mouseOn
    // priority: check for mouse over ports first, then symbols, then wires
    // the code for checking for mouse over ports is the same as in Sheet.mouseOn
    // otherwise symbol and wire mouseover is calculated based on intersection with mouseBoundingBox
    match intersectingWiresInfo, intersectingSymbolInfo with
    | _, Some symbolId ->
        let inputPorts, outputPorts =
            Symbol.getPortLocations model.Wire.Symbol [ ComponentId symbolId ]
            |> fun (x, y) -> Map.toList x, Map.toList y
        match mouseOnPort inputPorts pos 2.5 with
        | Some(portId, portLoc) -> "InputPort: ", portId.ToString()
        | None ->
            match mouseOnPort outputPorts pos 2.5 with
            | Some(portId, portLoc) -> "OutputPort: ", portId.ToString()
            | None -> "Symbol: ", symbolId.ToString()
    | Some wireId, _ -> "Wire: ", wireId.ToString()
    | _ -> "Component: ", "Nothing Selected"

/// Top Level function for developer mode
let developerModeView (model: ModelType.Model) dispatch =
    let sheetDispatch sMsg = dispatch (Sheet sMsg)

    let counterItems =
        [ ("Wire-Sym Intersects", (countVisibleSegsIntersectingSymbols model.Sheet).ToString())
          ("Wire-Wire Intersects", (countVisibleSegsPerpendicularCrossings model.Sheet).ToString())
          ("Sym-Sym Intersects", (countIntersectingSymbolPairs model.Sheet).ToString())
          ("90º Degree Wire Bends", (countVisibleBends model.Sheet).ToString())
          ("Near-Straight Wires", (countAlmostStraightWiresOnSheet model.Sheet).ToString())
          ("Straight Wires", (countStraightWiresOnSheet model.Sheet).ToString())
          ("Visible Seg. Length", (countVisibleSegmentLength model.Sheet).ToString("F1")) ]

    let trackingMenuItem trackingMenuName (cachedStringData: (string list) option) dispatch =
        Menu.Item.li
            [ (Menu.Item.IsActive(model.Tracking))
              Menu.Item.OnClick(fun _ ->
                  let cachedStringData =
                      if model.Tracking then
                          None
                      else
                          cachedStringData
                  dispatch (SelectTracking((not model.Tracking), cachedStringData))) ]
            [ strong [] [ str trackingMenuName ] ]


    /// Some instructions for the user (deprecated)
    let instructionText =
        div
            [ Style [ Margin "15px 0 200px 0" ] ]
            [ p [] [ str "Sample Text 1" ]
              p [] [ str "Sample Text 2" ]
              p [] [ str "Sample Text 3" ] ]

    /// Create a counter item (a title + number) for the sheet stats menu
    let createCounterItem title value =
        Level.item
            [ Level.Item.HasTextCentered ]
            [ div
                  []
                  [ Level.heading [] [ str title ]
                    strong [ Style [ FontSize "17px" ] ] [ str (value) ] ] ]

    /// Create a counter item that is dimmed, for the sheet stats menu
    let createCounterItemSaved title value =
        Level.item
            [ Level.Item.HasTextCentered ]
            [ div
                  []
                  [ Level.heading [] [ str title ]
                    strong [ Style [ FontSize "17px"; Color "#777" ] ] [ str (value) ] ] ]

    let trackerSetting =
        let cachedSheetStats = counterItems |> List.map snd

        div
            [ Style [ Margin "5px 0 10px 0" ] ]
            [ Level.level
                  []
                  [ Level.item
                        [ Level.Item.HasTextCentered ]
                        [ div
                              [ Style [ FontSize "14px"; Width "100%"; Border "1.1px solid #555" ] ]
                              [ Menu.list [] [ trackingMenuItem "Hold/Unhold Values" (Some cachedSheetStats) dispatch ] ] ]

                    ] ]

    /// Create a list of counter items for the sheet stats menu. Can be expanded to include more stats
    /// Functions take in a SheetT.Model and output a string/int/float
    let counters =
        let counterChunks = counterItems |> List.chunkBySize 2
        (counterChunks)
        |> List.map (fun counterChunk ->
            div
                [ Style [ Margin "0 0" ] ]
                [ Level.level
                      []
                      (counterChunk
                       |> List.map (fun (title, value) -> createCounterItem title value)) ])
        |> div []

    let savedCounters =
        match model.CachedSheetStats with
        | Some cachedSheetStats ->
            let savedCounterItems =
                cachedSheetStats
                |> List.map2 (fun (title, _) value -> title, value) counterItems
            let savedCounterChunks = savedCounterItems |> List.chunkBySize 2
            let savedCounters =
                (savedCounterChunks)
                |> List.map (fun counterChunk ->
                    div
                        [ Style [ Margin "0 0" ] ]
                        [ Level.level
                              []
                              (counterChunk
                               |> List.map (fun (title, value) -> createCounterItemSaved title value)) ])
                |> div []
            savedCounters
        | None -> div [] []

    let savedCountersWrapper =
        if model.Tracking then
            div [ Style [ Background "#f4f4f4"; Padding " 5px" ] ] [ savedCounters ]
        else
            div [] []

    /// Stores string details of the currently hovered comp to be used in sheetStatsMenu
    let hoveredType, hoveredId = findHoveredID model.Sheet.LastMousePos model.Sheet

    /// Stores the mouse position and hovered component data
    let mouseSensitiveData =
                    div
                        [ Style [ MarginBottom "20px" ] ]
                        [ strong [] [ str ("Mouse Position: ") ]
                          br []
                          code
                              []
                              [ str (
                                    (model.Sheet.LastMousePos.X.ToString("F2"))
                                    + ", "
                                    + (model.Sheet.LastMousePos.Y.ToString("F2"))
                                ) ]

                          br []
                          strong [] [ str ("Hovered " + hoveredType) ]
                          br []
                          code [] [ str (hoveredId) ] ]

    /// Contains the mouse position, hovered comp data, and the counters
    let sheetStatsMenu =
        details
            [ Open(model.SheetStatsExpanded) ]
            [ summary [ menuLabelStyle; OnClick(fun _ -> dispatch (ToggleSheetStats)) ] [ str "Sheet Stats " ]
              div
                  []
                  [
                    counters
                    trackerSetting
                    savedCountersWrapper ] ]

    /// Function to programmatically generate a html table from PortMaps.Order
    let createTableFromPortMapsOrder (map: Map<Edge, string list>) =
        Table.table
            []
            (map
             |> Map.toList
             |> List.map (fun (edge, strList) ->
                 tr
                     []
                     [ td [] [ str (edge.ToString()) ]
                       td
                           []
                           (strList
                            |> List.collect (fun s -> [ code [] [ str ("• " + s) ]; br [] ])) ]))

    /// Function to programmatically generate a html table from a Map PortMaps.Oritentation
    let createTableFromPorts (portsMap: Map<string, Edge>) (symbol: Symbol) =
        let referencePortTable =
            // get a list of ports from the selected component. more efficient to search smaller list
            // than looking of ports in model.Sheet.Wire.Symbol.Symbols
            symbol.Component.InputPorts
            @ symbol.Component.OutputPorts
            |> List.map (fun port -> port.Id, port)
            |> Map.ofList
        let portDetailMap =
            portsMap
            |> Map.map (fun key _ -> Map.tryFind key referencePortTable)
            |> Map.filter (fun _ value -> value.IsSome)
            |> Map.map (fun _ value -> value.Value)
        let tableRows =
            portDetailMap
            |> Map.toList
            |> List.map (fun (key, port) ->
                tr
                    []
                    [ td [] [ code [] [ str port.Id ] ]
                      td
                          []
                          [ str (
                                match port.PortNumber with
                                | Some num -> num.ToString()
                                | None -> "N/A"
                            ) ]
                      td
                          []
                          [ str (
                                match port.PortType with
                                | CommonTypes.PortType.Input -> "In"
                                | CommonTypes.PortType.Output -> "Out"
                            ) ]
                      td [] [ code [] [ str port.HostId ] ] ])
        Table.table
            []
            [ tr
                  []
                  [ th [] [ str "Port Id" ]
                    th [] [ str "No." ]
                    th [] [ str "I/O" ]
                    th [] [ str "Host Id" ] ]
              yield! tableRows ]

    /// Function to programmatically generate data for a symbol. Includes the symbol's data, its port data, and portmap
    let symbolToListItem (model: ModelType.Model) (symbol: Symbol) =
        let SymbolTableInfo =
            (Table.table
                [ Table.IsFullWidth; Table.IsBordered ]
                [ tbody
                      []
                      [ tr
                            []
                            [ td [] [ strong [] [ str "Id: " ] ]
                              td [] [ code [] [ str (symbol.Id.ToString()) ] ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "Pos: " ] ]
                              td
                                  []
                                  [ str (
                                        symbol.Pos.X.ToString("F2")
                                        + ", "
                                        + symbol.Pos.Y.ToString("F2")
                                    ) ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "Comp. Type: " ] ]
                              td [] [ code [] [ str (symbol.Component.Type.ToString()) ] ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "Comp. Label: " ] ]
                              td [] [ str (symbol.Component.Label.ToString()) ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "Comp. H,W: " ] ]
                              td
                                  []
                                  [ str (
                                        symbol.Component.H.ToString("F2")
                                        + ", "
                                        + symbol.Component.W.ToString("F2")
                                    ) ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "STransform: " ] ]
                              td
                                  []
                                  [ str (
                                        "Rotation: "
                                        + symbol.STransform.Rotation.ToString()
                                    )
                                    br []
                                    str ("flipped: " + symbol.STransform.flipped.ToString()) ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "HScale, VScale: " ] ]
                              td
                                  []
                                  [ (match symbol.HScale with
                                     | Some hscale -> str ("HScale: " + hscale.ToString("F2"))
                                     | None -> str "HScale: N/A")
                                    br []
                                    (match symbol.VScale with
                                     | Some vscale -> str ("VScale: " + vscale.ToString("F2"))
                                     | None -> str "VScale: N/A") ] ] ] ])

        // expandable menu persists between updates due to the model keeping track of the expanded state.
        // this is unlike the Catalogue menu that immediately shuts expandable menu when the user clicks away
        [ details
              [ Open(model.SymbolInfoTableExpanded) ]
              [ summary [ menuLabelStyle; OnClick(fun _ -> dispatch (ToggleSymbolInfoTable)) ] [ str "Symbol " ]
                div [] [ SymbolTableInfo ] ]
          details
              [ Open model.SymbolPortsTableExpanded ]
              [ summary [ menuLabelStyle; OnClick(fun _ -> dispatch (ToggleSymbolPortsTable)) ] [ str "Ports" ]
                div [] [ (createTableFromPorts symbol.PortMaps.Orientation symbol) ] ]
          details
              [ Open model.SymbolPortMapsTableExpanded ]
              [ summary [ menuLabelStyle; OnClick(fun _ -> dispatch (ToggleSymbolPortMapsTable)) ] [ str "PortMaps" ]
                div [] [ (createTableFromPortMapsOrder symbol.PortMaps.Order) ] ] ]

    /// Function to programmatically generate data for a wire. Includes the wire's data and its segments
    let wireToListItem (wire: Wire) =
        let WireTableInfo =
            (Table.table
                [ Table.IsFullWidth; Table.IsBordered ]
                [ tbody
                      []
                      [ tr
                            []
                            [ td [] [ strong [] [ str "WId: " ] ]
                              td [] [ code [] [ str (wire.WId.ToString()) ] ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "StartPos: " ] ]
                              td
                                  []
                                  [ str (
                                        wire.StartPos.X.ToString("F2")
                                        + ", "
                                        + wire.StartPos.Y.ToString("F2")
                                    ) ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "InputPort: " ] ]
                              td [] [ code [] [ str (wire.InputPort.ToString()) ] ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "OutputPort: " ] ]
                              td [] [ code [] [ str (wire.OutputPort.ToString()) ] ] ]
                        tr [] [ td [] [ strong [] [ str "Width: " ] ]; td [] [ str (wire.Width.ToString()) ] ]
                        tr
                            []
                            [ td [] [ strong [] [ str "InitialOrientation: " ] ]
                              td [] [ str (wire.InitialOrientation.ToString()) ] ] ] ])

        let createTableFromASegments (segments: ASegment list) =
            Table.table
                []
                [ tr
                      []
                      [ th [] [ str "Len" ]
                        th [] [ str "Start" ]
                        th [] [ str "End" ]
                        th [] [ str "Drag?" ]
                        th [] [ str "Route?" ] ]
                  yield!
                      segments
                      |> List.map (fun seg ->
                          tr
                              []
                              [ td [] [ str (sprintf "%.1f" seg.Segment.Length) ]
                                td [] [ str (sprintf "%.1f, %.1f" seg.Start.X seg.Start.Y) ]
                                td [] [ str (sprintf "%.1f, %.1f" seg.End.X seg.End.Y) ]

                                td
                                    []
                                    [ str (
                                          if seg.Segment.Draggable then
                                              "T"
                                          else
                                              "F"
                                      ) ]
                                td
                                    []
                                    [ str (
                                          match seg.Segment.Mode with
                                          | Manual -> "M"
                                          | Auto -> "A"
                                      ) ] ]) ]

        let absSegments = getAbsSegments wire
        let WireSegmentsTableInfo = createTableFromASegments absSegments

        [ details
              [ Open model.WireTableExpanded ]
              [ summary [ menuLabelStyle; OnClick(fun _ -> dispatch (ToggleWireTable)) ] [ str "Wire " ]
                div [] [ WireTableInfo ] ]
          details
              [ Open model.WireSegmentsTableExpanded ]
              [ summary [ menuLabelStyle; OnClick(fun _ -> dispatch (ToggleWireSegmentsTable)) ] [ str "Wire Segments" ]
                div [] [ WireSegmentsTableInfo ] ] ]

    /// Code taken from the Properties tab. If nothing is selected, a message is displayed.
    let viewComponent =
        match model.Sheet.SelectedComponents, model.Sheet.SelectedWires with
        | [ compId: ComponentId ], [] ->
            let comp = SymbolUpdate.extractComponent model.Sheet.Wire.Symbol compId
            let symbol: SymbolT.Symbol = model.Sheet.Wire.Symbol.Symbols[compId]

            div [ Key comp.Id ] [ ul [] (symbolToListItem model symbol) ]
        | [], [ wireId: ConnectionId ] ->
            let wire = model.Sheet.Wire.Wires.[wireId]
            div [ Key(wireId.ToString()) ] [ ul [] (wireToListItem wire) ]
        | _ ->
            match model.CurrentProj with
            | Some proj ->
                let sheetName = proj.OpenFileName
                let sheetLdc =
                    proj.LoadedComponents
                    |> List.find (fun ldc -> ldc.Name = sheetName)
                let sheetDescription = sheetLdc.Description

                div
                    []
                    [ p [] [ str "Select a component in the diagram to view its attributes." ]
                      br [] ]
            | None -> null

    /// Top level div for the developer mode view
    let viewComponentWrapper = div [] [ p [ menuLabelStyle ] []; viewComponent ]
    div [ Style [ Margin "-10px 0 20px 0" ] ] ([ mouseSensitiveData; sheetStatsMenu; viewComponentWrapper ])
