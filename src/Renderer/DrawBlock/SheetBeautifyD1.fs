﻿module SheetBeautifyD1
//-----------------Module for D1 beautify Helper functions--------------------------//
// I try to section the helpers out handling symbol/custom components
// next are wire helpers


open Optics
open Optics.Operators
open CommonTypes
open DrawModelType
open DrawModelType.SymbolT
open DrawModelType.BusWireT
open Helpers
open Symbol
open BlockHelpers
open SheetBeautifyHelpers
open RotateScale
open BusWire
    module D1Helpers =
        // D1 HELPER FUNCTIONS ----------------------------------------------------------------------
        (*type PortInfo =
            { port: Port
              sym: Symbol
              side: Edge
              ports: string list
              gap: float
              topBottomGap: float
              portDimension: float
              h: float
              w: float
              portGap: float }*)

        let alignComponents 
            (wModel: BusWireT.Model)
            (symA: Symbol)
            (portA: Port)
            (symB: Symbol)
            (portB: Port)
            : BusWireT.Model =
            
            // Only attempt to align symbols if they are connected by ports on parallel edges.
            let (movePortInfo, otherPortInfo) = (makePortInfo symA portA,makePortInfo symB portB) 

            let offset = alignPortsOffset movePortInfo otherPortInfo
            let symbol' = moveSymbol offset symA
            let model' = Optic.set (symbolOf_ symA.Id) symbol' wModel
            BusWireSeparate.routeAndSeparateSymbolWires model' symA.Id



        /// returns all singly compoenents with one output xor input port
        let getSinglyComp (wModel: BusWireT.Model): Symbol list option =
            Optic.get symbols_ wModel.Symbol
            |> Map.fold (fun acc (sId:ComponentId) (sym:SymbolT.Symbol) ->
                match getComponentProperties sym.Component.Type sym.Component.Label with
                | (0, 1, _, _) -> sym :: acc
                | (1, 0, _, _) -> sym :: acc
                | _ -> acc) []
            |> List.rev
            |> fun result -> 
                if List.isEmpty result then None
                else Some result

    
        /// returns all multiply connected components on a sheet
        let getMultiplyComp (wModel: BusWireT.Model): Symbol list option =
            Optic.get symbols_ wModel.Symbol
            |> Map.fold (fun acc (sId:ComponentId) (sym:SymbolT.Symbol) ->
                let (numInputs, numOutputs, _, _) = getComponentProperties sym.Component.Type sym.Component.Label
                match (numInputs, numOutputs) with
                | (i, o) when i > 1 || o > 1 -> sym :: acc  // Components with more than one input or output
                | _ -> acc) []  // Ignore components with one or no inputs/outputs
            |> List.rev
            |> fun result -> 
                if List.isEmpty result then None
                else Some result

        /// For a list of connecting Symbol Pairs, return the list of pairs where both symbols are custom Components
        let filterCustomComp (portPair: (Symbol * Symbol) list): ((Symbol * Symbol) list) =
            portPair
            |> List.filter (fun (syma, symb) -> 
                match syma.Component.Type, symb.Component.Type with
                | Custom _, Custom _ -> true
                | _ -> false)

    
        
        /// returns the ports for a given symbol
        let getPortSym (model: SymbolT.Model) (sym: Symbol): Map<string,Port>  =
            let portId = sym ^. (portMaps_ >-> orientation_) |> Map.keys
            portId
            |> Seq.fold (fun acc id -> 
                match Map.tryFind id model.Ports with
                | Some port -> Map.add id port acc
                | None -> acc) Map.empty
    
        /// Finds the corresponding WId(s) and other port(s) for a given Port based on connections.
        /// `port` is the given port to find matches for.
        /// `connections` is the list of all connections to search through.
        let findOtherPorts (port: Port) (connections: Connection list): (string * Port) list =
            printfn $"port {port}"
            connections |> List.collect (fun connection ->
                if connection.Source.Id = port.Id then
                    [(connection.Id,connection.Target)]
                elif connection.Target.Id = port.Id then
                    [(connection.Id,connection.Source)]
                else
                    [] // No match, proceed to the next connection
            )
        
        /// return the corresponding Symbol for a given Port
        let getSym (wModel:BusWireT.Model) (port:Port): Symbol =
            let sym =
                port.HostId
                |> ComponentId
                |> fun id -> wModel.Symbol.Symbols[id]
            sym

        /// For a given symbol and edge, will return the number of ports on that edge
        let getPortCount (sym: Symbol) (edge: Edge): int =
            sym 
            |> Optic.get portMaps_
            |> (fun pm -> pm.Order)
            |> (fun orderMap -> Map.find edge orderMap)
            |> List.length

        /// Returns the offset of two ports relative to portA
        let getPortOffset (wModel:BusWireT.Model) (portA: Port) (portB:Port): XYPos =
            let symA = getSym wModel portA
            let symB = getSym wModel portB
            let offset =
                {
                    X = (calculatePortRealPos (makePortInfo symB portB)).X - (calculatePortRealPos (makePortInfo symA portA)).X;
                    Y = (calculatePortRealPos (makePortInfo symB portB)).Y - (calculatePortRealPos (makePortInfo symA portA)).Y
                }
            offset

        /// For Components with multiple output ports, will return the most suitable Port to align
        let choosePortAlign (wModel:BusWireT.Model) (ports: Port list): Port * Port =
            let connections = extractConnections wModel
            let portPair =
                ports
                |> List.collect (fun port -> 
                    let portOrientation = getSymbolPortOrientation (getSym wModel port) port
                    findOtherPorts port connections
                    |> List.choose (fun (_, otherPort) ->
                        let otherPortOrientation = getSymbolPortOrientation (getSym wModel otherPort) otherPort
                        if portOrientation = otherPortOrientation.Opposite then
                            let offsetXY = getPortOffset wModel port otherPort
                            let offset = match portOrientation with
                                         | Top | Bottom -> offsetXY.X
                                         | Left | Right -> offsetXY.Y
                            Some (offset, (port, otherPort))
                        else
                            None))
                |> List.minBy fst
                |> snd
            portPair
        

        /// Chooses other Symbol to align a port based on the condition that they are opposite parallel edges
        /// and will choose the symbol with the smallest offset distance to help avoid symbol overlaps
        let chooseSymAlign (port: Port) (otherPorts: (string * Port) list) (wModel:BusWireT.Model): (Symbol * Port) Option=
            if List.length otherPorts >1 then
                let portOrientation = getSymbolPortOrientation (getSym wModel port) port
                
                let filteredAndMappedPorts =
                    otherPorts
                    |> List.choose (fun (_, otherPort) ->
                        let otherPortOrientation = getSymbolPortOrientation (getSym wModel otherPort) otherPort
                        if portOrientation = otherPortOrientation.Opposite then //check if the other component port are on opposite edges
                            let offsetXY = getPortOffset wModel port otherPort
                            let offset =
                                match portOrientation with
                                | Top | Bottom -> offsetXY.X
                                | Left | Right -> offsetXY.Y
                            Some (offset, otherPort)
                        else
                            None) 
                match filteredAndMappedPorts with
                | [] -> None // No matching ports
                | _ ->
                    filteredAndMappedPorts
                    |> List.minBy fst //Choose the port with the smallest offset (X Y Is chosen depending on orientation)
                    |> snd
                    |> (fun port -> Some ((getSym wModel port),port))
            else
                let otherSym =
                    otherPorts
                    |> List.tryHead
                    |> Option.map (fun (_,port) -> ((getSym wModel port),port))
                otherSym
    
        
        /// If SymA and SymB have multiple Parallel lines connected, Will resize either SymA or SymB to align ports
        /// This will only work for custom components
        let scaleMultiplyComp (wModel: BusWireT.Model) (syma: Symbol) (edgea:Edge) (symb: Symbol) (edgeb:Edge) : BusWireT.Model =
            // Determine scaleSym and otherSym based on port counts
            let scaleSym, otherSym =
                if (getPortCount syma edgea < getPortCount symb edgeb) then symb, syma else syma, symb
            let scaleSym' = reSizeSymbol wModel scaleSym otherSym
            let wModel' = Optic.set (symbolOf_ scaleSym.Id) scaleSym' wModel
            BusWireSeparate.routeAndSeparateSymbolWires wModel' scaleSym.Id

        let alignMultiplyComp (wModel: BusWireT.Model) (syms: Symbol list): BusWireT.Model =
            syms
            |> List.fold (fun wModel' sym ->
                let outputPorts = getPortSym wModel'.Symbol sym
                                 |> Map.filter (fun _ port -> port.PortType = PortType.Output)
                                 |> Map.values
                                 |> Seq.toList
                match outputPorts with
                | [] -> wModel' // If there are no output ports, return the sheet as is
                | _ -> 
                    let portPair = choosePortAlign wModel' outputPorts
                    let otherPort = snd portPair
                    let otherSym = getSym wModel' otherPort
                    alignComponents  wModel' sym (fst portPair) otherSym otherPort
             ) wModel
               
               
    module Beautify =
        open D1Helpers

        let alignSinglyComp (wModel: BusWireT.Model): BusWireT.Model =
            let singlyComp = getSinglyComp wModel
            printfn $"singlyCompsize: {match singlyComp with
                                        | Some(lst) -> List.length lst
                                        | None -> 0 (* or handle the case where there is no list *)
                                        }"
            let connections = extractConnections wModel
            match singlyComp with
            | Some syms ->
                List.fold (fun wModel' sym ->
                    let portsMap = getPortSym wModel'.Symbol sym
                    // Since it's a singly component, assume there is only one port
                    printfn $"Portsmap size: {Map.count portsMap}"
                    let portOption = portsMap |> Map.values |> Seq.tryHead
                    printfn $"PortOption: {match portOption with
                                            | Some(port) -> port.Id
                                            | None -> string 0}"
                    match portOption with
                    | Some port ->
                        printfn $"Connections: {List.length connections}"
                        let otherPorts = findOtherPorts port connections
                        printfn $"OtherPorts: {List.length otherPorts}"
                        let otherSymOpt = chooseSymAlign port otherPorts wModel'
                        match otherSymOpt with
                        | Some (otherSym,otherPort) ->
                            printfn $"portHost: {sym.Component.Label}"
                            printfn $"portType: {port.PortType}"
                            printfn $"otherportHost: {otherSym.Component.Label}"
                            printfn $"otherportType: {port.PortType}"
                            let wModel'' = alignComponents  wModel' sym port otherSym otherPort
                            wModel''
                        | None ->
                            printfn $"Fail Match"
                            wModel' // If no suitable alignment, keep the model unchanged
                    | None ->
                        printfn $"No PortOption"
                        wModel' // If no port is found, proceed to the next symbol                            
                ) wModel syms
            | None ->
                printfn $"ERROR"
                wModel // If getSinglyComp returns None, return the model unchanged


    

        let alignScaleComp (wModel: BusWireT.Model): BusWireT.Model =
            let multiplySyms = getMultiplyComp wModel
            let connPairs = getConnSyms wModel
            let custPairs = filterCustomComp connPairs
            let customSymbols = custPairs |> List.collect (fun (a, b) -> [a; b])

            let alignSyms = 
                match multiplySyms with
                | Some multiplySyms ->
                    List.filter (fun sym -> not (List.contains sym customSymbols)) multiplySyms
                | None -> [] // If there are no multiplySyms, use an empty list for alignSyms
                
            // Iterate over each pair in custPairs for processing
            let wModel' =
                if List.isEmpty custPairs then
                    wModel
                else
                    List.fold (fun wModel' (symA, symB) -> 
                        match getOppEdgePortInfo wModel' symA symB with
                        | Some (portInfoA, portInfoB) ->
                            let custEdges = (portInfoA.side, portInfoB.side)
                            scaleMultiplyComp wModel' symA (fst custEdges) symB (snd custEdges)                   
                        | None -> wModel'
                    ) wModel custPairs
                
            let wModel'' = 
                match alignSyms with
                | [] -> wModel' // If no symbols to align, skip this step
                | _ -> alignMultiplyComp wModel' alignSyms // Proceed with alignment if there are symbols to align

            // Return the final model after adjustments
            wModel''

        let sheetSingly (sheet: SheetT.Model): SheetT.Model =
            let singlyModel = alignSinglyComp sheet.Wire
            let sheet' = Optic.set SheetT.wire_ singlyModel sheet
            sheet'

        let sheetMultiply (sheet: SheetT.Model): SheetT.Model =
            let multiplyModel = alignScaleComp sheet.Wire
            let multiplySheet = Optic.set SheetT.wire_ multiplyModel sheet
            multiplySheet

        let sheetAlignScale (sheet: SheetT.Model): SheetT.Model =
           let multiplyModel = alignScaleComp sheet.Wire
           let multiplySheet = Optic.set SheetT.wire_ multiplyModel sheet
           let singlyModel = alignSinglyComp multiplySheet.Wire
           let sheet' = Optic.set SheetT.wire_ singlyModel multiplySheet
           sheet'  
