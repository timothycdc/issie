﻿module RotateScale

open CommonTypes
open DrawModelType
open DrawModelType.SymbolT
open DrawModelType.BusWireT
open SymbolUpdate
open Symbol
open Optics
open Operators
open BlockHelpers
open SymbolResizeHelpers

(*
    HLP23: This module will normally be used exclusively by team member doing the "smart resize symbol"
    part of the individual coding. During group phase work how it is used is up to the
    group. Functions from other members MUST be documented by "HLP23: AUTHOR" XML
    comment as in SmartHelpers.

    Normally it will update multiple wires and one symbols in the BusWire model so could use the SmartHelper
    function for the wires.
*)

/// Record containing all the information required to calculate the position of a port on the sheet.
type PortInfo =
    { port: Port
      sym: Symbol
      side: Edge
      ports: string list
      gap: float
      topBottomGap: float
      portDimension: float
      h: float
      w: float
      portGap: float }

/// TODO: this is mostly copy pasted code from Symbol.getPortPos, perhaps abstract out the existing code there to use makePortInfo.
/// Could not simply use getPortPos because more data (side, topBottomGap, etc.) is needed to caclulate the new dimensions of the resized symbol.
let makePortInfo (sym: Symbol) (port: Port) =
    let side = getSymbolPortOrientation sym port
    let ports = sym.PortMaps.Order[side] //list of ports on the same side as port
    let gap = getPortPosEdgeGap sym.Component.Type
    let topBottomGap = gap + 0.3 // extra space for clk symbol
    let portDimension = float ports.Length - 1.0
    let h, w = getRotatedHAndW sym

    let portGap =
        match side with
        | Left
        | Right -> float h / (portDimension + 2.0 * gap)
        | Bottom
        | Top -> float w / (portDimension + 2.0 * topBottomGap)

    { port = port
      sym = sym
      side = side
      ports = ports
      gap = gap
      topBottomGap = topBottomGap
      portDimension = portDimension
      h = h
      w = w
      portGap = portGap }

type wireSymbols = { symA: Symbol; symB: Symbol; wire: Wire }

let getPortAB wModel wireSyms =
    let ports = portsOfWires wModel [ wireSyms.wire ]
    let portA = filterPortBySym ports wireSyms.symA |> List.head
    let portB = filterPortBySym ports wireSyms.symB |> List.head
    portA, portB

/// Try to get two ports that are on opposite edges.
let getOppEdgePortInfo
    (wModel: BusWireT.Model)
    (symbolToSize: Symbol)
    (otherSymbol: Symbol)
    : (PortInfo * PortInfo) option
    =
    let wires = wiresBtwnSyms wModel symbolToSize otherSymbol

    let tryGetOppEdgePorts (wireSyms: wireSymbols) =
        let portA, portB = getPortAB wModel wireSyms
        let edgeA = getSymbolPortOrientation wireSyms.symA portA
        let edgeB = getSymbolPortOrientation wireSyms.symB portB

        match edgeA = edgeB.Opposite with
        | true -> Some(makePortInfo wireSyms.symA portA, makePortInfo wireSyms.symB portB)
        | _ -> None

    wires
    |> List.tryPick (fun w -> tryGetOppEdgePorts { symA = symbolToSize; symB = otherSymbol; wire = w })

let alignPortsOffset (movePInfo: PortInfo) (otherPInfo: PortInfo) =
    let getPortRealPos pInfo =
        getPortPos pInfo.sym pInfo.port + pInfo.sym.Pos

    let movePortPos = getPortRealPos movePInfo
    let otherPortPos = getPortRealPos otherPInfo
    let posDiff = otherPortPos - movePortPos

    match movePInfo.side with
    | Top
    | Bottom -> { X = posDiff.X; Y = 0.0 }
    | Left
    | Right -> { X = 0.0; Y = posDiff.Y }

let alignSymbols (wModel: BusWireT.Model) (symbolToSize: Symbol) (otherSymbol: Symbol) : BusWireT.Model =

    // Only attempt to align symbols if they are connected by ports on parallel edges.
    match getOppEdgePortInfo (wModel: BusWireT.Model) symbolToSize otherSymbol with
    | None -> wModel
    | Some(movePortInfo, otherPortInfo) ->
        let offset = alignPortsOffset movePortInfo otherPortInfo
        let symbol' = moveSymbol offset symbolToSize
        let model' = Optic.set (symbolOf_ symbolToSize.Id) symbol' wModel
        BusWireSeparate.routeAndSeparateSymbolWires model' symbolToSize.Id

/// HLP23: A helper function for reSizeSymbol that calculates the h,w, the resizedDimensions for a CustomComponent to be resized, ensuring straight wires
// This was originally part of reSizeSymbol, but was separated to become more modular.
// The logic is also clearer, using inline functions to speed up and properly visualise the difference between
// resizing using the gap or topBottomGap.
let calculateResizedDimensions (resizePortInfo: PortInfo) (otherPortInfo: PortInfo) =
    let inline resizedWithGap gapMultiplier =
        otherPortInfo.portGap
        * (resizePortInfo.portDimension + 2.0 * gapMultiplier)

    match resizePortInfo.side with
    | Left
    | Right -> (resizedWithGap resizePortInfo.gap), resizePortInfo.w

    | Top
    | Bottom -> resizePortInfo.h, (resizedWithGap resizePortInfo.topBottomGap)

/// HLP23: A helper function that takes in two symbols connected by wires, symbolToSize and otherSymbol.
/// The first symbol must be a Custom component. The function adjusts the size of symbolToSize so that the wires
/// connecting it with the otherSymbol become exactly straight/
// Comments here were improved, and the calculation logic for h and w was moved to calculateResizedDimensions.
// In the last part of matching with the Custom type, I removed the occurences of 'lets'
// and instead used a pipeline to make the code more readable. This also has the added benefit of showing the
// types of the variables at each stage of the pipeline, changing as it goes
let reSizeSymbol (wModel: BusWireT.Model) (symbolToSize: Symbol) (otherSymbol: Symbol) : (Symbol) =
    let wires = wiresBtwnSyms wModel symbolToSize otherSymbol

    // Try to get the PortInfo of two ports that are on opposite edges of two different symbols, if none found just use any two ports.
    let resizePortInfo, otherPortInfo =
        match getOppEdgePortInfo wModel symbolToSize otherSymbol with
        | None ->
            let pA, pB =
                getPortAB wModel { symA = symbolToSize; symB = otherSymbol; wire = wires.Head }
            makePortInfo symbolToSize pA, makePortInfo symbolToSize pB
        | Some(pIA, pIB) -> (pIA, pIB)

    let h, w = calculateResizedDimensions resizePortInfo otherPortInfo

    match symbolToSize.Component.Type with
    | Custom _ ->
        let scaledSymbol = setCustomCompHW h w symbolToSize
        let scaledInfo = makePortInfo scaledSymbol resizePortInfo.port
        let offset = alignPortsOffset scaledInfo otherPortInfo
        moveSymbol offset scaledSymbol
    | _ -> symbolToSize

/// For UI to call ResizeSymbol.
// More pipelines were used here to improve readability
let reSizeSymbolTopLevel (wModel: BusWireT.Model) (symbolToSize: Symbol) (otherSymbol: Symbol) : BusWireT.Model =
    printfn $"ReSizeSymbol: ToResize:{symbolToSize.Component.Label}, Other:{otherSymbol.Component.Label}"

    let scaledSymbol = reSizeSymbol wModel symbolToSize otherSymbol

    wModel
    |> Optic.set (symbolOf_ symbolToSize.Id) scaledSymbol
    |> (fun model' -> BusWireSeparate.routeAndSeparateSymbolWires model' symbolToSize.Id)

(* mhc21
Changes to RotateScale (specifically optimiseSymbol and fuctions/dataTypes related to it):

(1) Changed SymConnDataT so that it's Map<Symbol*Edge, Int> from Map<ComponentId*Edge, Int>.
    This is because having it as cid (instead of symbol) before was redundant and used to
    fetch the otherSymbol anyway: "let otherSym = Optic.get (symbolOf_ cid) wModel ".

(2) Made updateData function into a single match case function, making it simpler to read.
    Before there were many unecessary match statements that would find "otherSymbol",
    "check opposite symbol edge", etc which could all be combined into 1 match as all
    the needed information was already there (Note: This was also possible as I changed
    the function to tryWireSymOppEdge to updateConnMap in the next point).

(3) Changed tryWireSymOppEdge to updateConnMap which deals with updating the ConnMap now,
    dependent on if the symbols connect on opposite edges. This saved space/lines of
    unnecessary code as tryWireSymOppEdge would return an option to optimiseSymbol,
    which from there then calls updateOrInsert depending on the result.
    Instead just have it all done in tryWireSymmOppEdge(which is now renamed updateConnMap).

(4) Made folder slightly easier to read by removing brackets/variables that were unnecessary.
    What was before "(symCount: ((ComponentId * Edge) * int) array)" is now
    "(connSorted: (Symbol * Edge) array)". This was possible due to the change of
    SymConnDataT type and only taking "fst" of its value.

(5) Added XML comments above functions and next to confusing lines, specifying their purpose.

(6) Changed a few variable names to make them more readable and appropriate.
*)

/// Symbol = The otherSymbol connected to this symbol
/// Edge = This symbol edge
/// Int = count (No of times otherSymbol is connected to this symbol edge)
type SymConnDataT = { ConnMap: Map<Symbol * Edge, int> }

// Updates/Inserts the "edge" and "otherSym" that is connected to target symbol
let updateOrInsert (symConn: SymConnDataT) (edge: Edge) (otherSym: Symbol) : SymConnDataT =
    let m = symConn.ConnMap
    let count =
        Map.tryFind (otherSym, edge) m
        |> Option.defaultValue 0
        |> (+) 1 // Finds key "(otherSym, edge)", update its "No of connections" if exist, else insert
    { ConnMap = Map.add (otherSym, edge) count m } // Updates/Inserts "(otherSym, edge)" with value "count"

/// If a wire between a target symbol and another symbol connects opposite edges, return and update the symmConnData, else return symConn
let updateConnMap (wModel: Model) (wire: Wire) (symConn: SymConnDataT) (sym: Symbol) (otherSym: Symbol) : SymConnDataT =
    let symEdge = wireSymEdge wModel wire sym
    let otherSymEdge = wireSymEdge wModel wire otherSym

    match symEdge = otherSymEdge.Opposite with
    | true -> updateOrInsert symConn symEdge otherSym
    | _ -> symConn

// TODO: this is copied from Sheet.notIntersectingComponents. It requires SheetT.Model, which is not accessible from here. Maybe refactor it.
let noSymbolOverlap (boxesIntersect: BoundingBox -> BoundingBox -> bool) boundingBoxes sym =
    let symBB = getSymbolBoundingBox sym

    boundingBoxes
    |> Map.filter (fun sId boundingBox -> boxesIntersect boundingBox symBB && sym.Id <> sId)
    |> Map.isEmpty

/// Finds the optimal size and position for the selected symbol w.r.t. to its surrounding symbols.
let optimiseSymbol
    (wModel: BusWireT.Model)
    (symbol: Symbol) // Target symbol
    (boundingBoxes: Map<CommonTypes.ComponentId, BoundingBox>)
    : BusWireT.Model
    =

    // If a wire connects "symbol" to different symbol, note which edge (of "symbol") it is connected to
    let updateData (symConn: SymConnDataT) _ (wire: Wire) : SymConnDataT =
        let symS, symT = getSourceSymbol wModel wire, getTargetSymbol wModel wire // Get both symbols at each end of the wire

        match symS, symT with
        | _ when (symS.Id <> symbol.Id) && (symT.Id = symbol.Id) -> updateConnMap wModel wire symConn symbol symS // symT="symbol" and symS=otherSymbol
        | _ when (symS = symbol) && (symT <> symbol) -> updateConnMap wModel wire symConn symbol symT // symS="symbol" and symT=otherSymbol
        | _ -> symConn // symS or symT are not "symbol"

    let symConn =
        ({ ConnMap = Map.empty }, wModel.Wires)
        ||> Map.fold updateData // Look through all wires to build and accumulate SymConnDataT.

    let tryResize (connSorted: (Symbol * Edge) array) (sym: Symbol) : Symbol =

        let alignSym (sym: Symbol) (otherSym: Symbol) =
            let resizedSym = reSizeSymbol wModel sym otherSym
            let noOverlap = noSymbolOverlap DrawHelpers.boxesIntersect boundingBoxes resizedSym

            match noOverlap with
            | true -> true, resizedSym
            | _ -> false, sym

        let folder
            ((hAligned, vAligned, sym): bool * bool * Symbol)
            ((otherSym, edge): Symbol * Edge)
            : (bool * bool * Symbol)
            =
            match hAligned, vAligned with
            | false, _ when edge = Top || edge = Bottom -> // Try to align horizontally
                let hAligned', resizedSym = alignSym sym otherSym
                (hAligned', vAligned, resizedSym)
            | _, false when edge = Left || edge = Right -> // Try to align vertically
                let vAligned', resizedSym = alignSym sym otherSym
                (hAligned, vAligned', resizedSym)
            | _ -> (hAligned, vAligned, sym) // Else already aligned so return sym

        ((false, false, sym), connSorted)
        ||> Array.fold folder // Goes through each conn and tries to make sym "hAligned" and "vAligned"
        |> (fun (hAligned, vAligned, sym) -> sym) // Return just the sym

    let scaledSymbol =
        let connSorted =
            Map.toArray symConn.ConnMap
            |> Array.filter (fun (_, count) -> count > 1)
            |> Array.sortByDescending snd // Sort by count
            |> Array.map fst // Only need the (symbol * edge)
        tryResize connSorted symbol

    let model' = Optic.set (symbolOf_ symbol.Id) scaledSymbol wModel
    BusWireSeparate.routeAndSeparateSymbolWires model' symbol.Id

(*
    HLP23: this is a placeholder module for some work that can be done in the individual or team phase
    but has not been given a "starter" by me. however it is pretty easy to get started on it.

    This code would be HIGHLY USEFUL (especially the "scaling" option).

    Currently if multiple symbols are selected and rotated, each symbol will rotate, but the positions
    of teh symbols will stay fixed. The desired function for the entire block of symbols to rotate,
    applying 2-D rotation 90 degree rotation and flipping to the symbol positions about the centre of
    the block of selected symbols.

    This operation has "simple" and "better" implementations, like all the initial tasks:
    1. Rotate all symbols and wires exact - do not allow custom components
    2. Rotate all symbols, allow custom components, CC wires will change due to CC shape changes,
      and must be partial autorouted.
    3. Allow scaling as well as rotation (autoroute wires since components will not scale and therefore
      exact wire shape will change). This operation can include Custom components since all wires are
      autorouted anyway.
    Driver test code can easily be adapted from existing Smart module Test menu items. Menu commands
    which operate on selected symbols - the tests will be more or less how this operation is actually used).

    One key UI challenge for SmartRotate is that when a block of symbols is rotated it may overlap other
    symbols. To allow valid placement it should be possible to move the block on the sheet until a place
    to drop it is found, using an interface identical to the "copy" and "paste" interface - which works
    fine with multiple symbols (try it). It should be possible to use that exact same interface by placing
    the rotated blokc into the copy buffer (not sure - maybe the copy buffer will need to be modified a bit).

    This could be ignored initially writing code, but muts be addressed somehow for teh operation to be usable.

    Those interested can ask me for details.
*)

(*HLP23: AUTHOR Ismagilov
  SheetUpdate.fs: 'Rotate' and 'Flip' msg in update function is replaced with this Smart implentation of rotate and flip.
  DrawModelType.fs: Added type ScaleType in SymbolT Module, which Distinguishes the type of scaling the user does.

  Added 2 keyboard messages in Renderer (CrtlU & CrtrlI) to scale the block of symbols up and down respectively.
  Invalid placement handled by giving model action drag and drop, therefore requiring user to place down/continue changing until valid

  SmartHelpers.fs contains all helper functions, e.g Rotating/Flipping symbols or points in general about any center point,
  as opposed to original rotate/flip functions.
*)

/// <summary>HLP 23: AUTHOR Ismagilov - Get the bounding box of multiple selected symbols</summary>
/// <param name="symbols"> Selected symbols list</param>
/// <returns>Bounding Box</returns>
/// Modifications to this block: Added inline functions to abstract away the tuples accesses for getRotatedHAndW.
/// Used inline functions to improve performance.
/// Previously using MaxBy was used to obtain the maxXsym, but this involved another redundant calculation to find maxXsym.Pos.X + getRotatedWidth symbol.
/// Changing to List.map means we keep only need to run symbols once to compute each symbol.Pos.X + getRotatedWidth symbol when finding the maxX
/// Same goes for the maxY,
/// Used List.Max instead of List.MaxBy to find min values, so we don't have to access symbol.Pos.(x or y) again
/// Also, this would break other functionality, but I propose renaming getBlock as getBoundingBoxFromSyms, as 'block' is not a usual term
let getBlock (symbols: Symbol list) : BoundingBox =
    // Define helper functions to get the rotated width and height of a symbol
    let inline getRotatedWidth symbol = snd (getRotatedHAndW symbol)
    let inline getRotatedHeight symbol = fst (getRotatedHAndW symbol)

    // Calculate the maximum X position in the bounding box plus considering its rotated width
    let maxX =
        symbols
        |> List.map (fun symbol -> symbol.Pos.X + getRotatedWidth symbol)
        |> List.max

    // Calculate the minimum X position in the bounding box
    let minX =
        symbols
        |> List.map (fun symbol -> symbol.Pos.X)
        |> List.min

    // Calculate the maximum Y position in the bounding box plus considering its rotated height
    let maxY =
        symbols
        |> List.map (fun symbol -> symbol.Pos.Y + getRotatedHeight symbol)
        |> List.max

    // Calculate the minimum Y position in the bounding box
    let minY =
        symbols
        |> List.map (fun symbol -> symbol.Pos.Y)
        |> List.min

    { TopLeft = { X = minX; Y = minY }; W = maxX - minX; H = maxY - minY }

/// <summary>HLP 23: AUTHOR Ismagilov - Takes a point Pos, a centre Pos, and a rotation type and returns the point flipped about the centre</summary>
/// <param name="point"> Original XYPos</param>
/// <param name="center"> The center XYPos that the point is rotated about</param>
/// <param name="rotation"> Clockwise or Anticlockwise </param>
/// <returns>New flipped point</returns>
let rotatePointAboutBlockCentre (point: XYPos) (centre: XYPos) (rotation: Rotation) =
    let relToCentre = (fun x -> x - centre)
    let rotAboutCentre (pointIn: XYPos) =
        match rotation with
        | Degree0 -> pointIn
        | Degree90 -> { X = pointIn.Y; Y = -pointIn.X }
        | Degree180 -> { X = -pointIn.X; Y = -pointIn.Y }
        | Degree270 -> { X = -pointIn.Y; Y = pointIn.X }

    let relToTopLeft = (fun x -> centre - x)

    point
    |> relToCentre
    |> rotAboutCentre
    |> relToTopLeft

/// <summary>HLP 23: AUTHOR Ismagilov - Takes a point Pos, a centre Pos, and a flip type and returns the point flipped about the centre</summary>
/// <param name="point"> Original XYPos</param>
/// <param name="center"> The center XYPos that the point is flipped about</param>
/// <param name="flip"> Horizontal or Vertical flip</param>
/// <returns>New flipped point</returns>
/// Improved readability of match case statements
let flipPointAboutBlockCentre (point: XYPos) (center: XYPos) (flip: FlipType) =
    match flip with
    | FlipHorizontal -> { X = center.X - (point.X - center.X); Y = point.Y }
    | FlipVertical -> { Y = center.Y - (point.Y - center.Y); X = point.X }

/// <summary>HLP 23: AUTHOR Ismagilov - Get the new top left of a symbol after it has been rotated</summary>
/// <param name="rotation"> Rotated CW or AntiCW</param>
/// <param name="h"> Original height of symbol (Before rotation)</param>
/// <param name="w"> Original width of symbol (Before rotation)</param>
/// <param name="sym"> Symbol</param>
/// <returns>New top left point of the symbol</returns>
///
///
///
/// Improved readability of match case statements
let adjustPosForBlockRotation (rotation: Rotation) (h: float) (w: float) (pos: XYPos) : XYPos =
    let posOffset =
        match rotation with
        | Degree0 -> { X = 0; Y = 0 }
        | Degree90 -> { X = (float) h; Y = 0 }
        | Degree180 -> { X = (float) w; Y = -(float) h }
        | Degree270 -> { X = 0; Y = (float) w }
    pos - posOffset

/// <summary>HLP 23: AUTHOR Ismagilov - Get the new top left of a symbol after it has been flipped</summary>
/// <param name="flip">  Flipped horizontally or vertically</param>
/// <param name="h"> Original height of symbol (Before flip)</param>
/// <param name="w"> Original width of symbol (Before flip)</param>
/// <param name="sym"> Symbol</param>
/// <returns>New top left point of the symbol</returns>
///
/// Improved readability of match case statements
let adjustPosForBlockFlip (flip: FlipType) (h: float) (w: float) (pos: XYPos) =
    let posOffset =
        match flip with
        | FlipHorizontal -> { X = (float) w; Y = 0 }
        | FlipVertical -> { X = 0; Y = (float) h }
    pos - posOffset

/// <summary>HLP 23: AUTHOR Ismagilov - Rotate a symbol in its block.</summary>
/// <param name="rotation">  Clockwise or Anticlockwise rotation</param>
/// <param name="block"> Bounding box of selected components</param>
/// <param name="sym"> Symbol to be rotated</param>
/// <returns>New symbol after rotated about block centre.</returns>
///
///
/// Replaced sym.Component and sym.STransform by denoting them with shorter identifiers adhering to DUI
/// Made match case statements more readable
let rotateSymbolInBlock (rotation: Rotation) (blockCentre: XYPos) (sym: Symbol) : Symbol =

    let h, w = getRotatedHAndW sym

    let comp = sym.Component
    let STransform = sym.STransform

    let newTopLeft =
        rotatePointAboutBlockCentre sym.Pos blockCentre (invertRotation rotation)
        |> adjustPosForBlockRotation (invertRotation rotation) h w

    let newComponent = { comp with X = newTopLeft.X; Y = newTopLeft.Y }

    let newSTransform =
        match STransform.flipped with
        | true ->
            { STransform with
                Rotation = combineRotation (invertRotation rotation) STransform.Rotation }
        | _ -> { STransform with Rotation = combineRotation rotation STransform.Rotation }

    { sym with
        Pos = newTopLeft
        PortMaps = rotatePortInfo rotation sym.PortMaps
        STransform = newSTransform
        LabelHasDefaultPos = true
        Component = newComponent }
    |> calcLabelBoundingBox

/// <summary>HLP 23: AUTHOR Ismagilov - Flip a symbol horizontally or vertically in its block.</summary>
/// <param name="flip">  Flip horizontally or vertically</param>
/// <param name="block"> Bounding box of selected components</param>
/// <param name="sym"> Symbol to be flipped</param>
/// <returns>New symbol after flipped about block centre.</returns>
///
/// Replaced sym.Component, sym.STransform and sym.PortMaps by denoting them with shorter useful identifiers that adhere to DUI
/// Made pipelining and match case statements more readable
let flipSymbolInBlock (flip: FlipType) (blockCentre: XYPos) (sym: Symbol) : Symbol =

    let comp = sym.Component
    let STransform = sym.STransform
    let PortMaps = sym.PortMaps

    let h, w = getRotatedHAndW sym
    //Needed as new symbols and their components need their Pos updated (not done in regular flip symbol)
    let newTopLeft =
        flipPointAboutBlockCentre sym.Pos blockCentre flip
        |> adjustPosForBlockFlip flip h w

    let portOrientation =
        PortMaps.Orientation
        |> Map.map (fun id side -> flipSideHorizontal side)

    let flipPortList currPortOrder side =
        currPortOrder
        |> Map.add (flipSideHorizontal side) PortMaps.Order[side]

    let portOrder =
        (Map.empty, [ Edge.Top; Edge.Left; Edge.Bottom; Edge.Right ])
        ||> List.fold flipPortList
        |> Map.map (fun edge order -> List.rev order)

    let newSTransform =
        { flipped = not STransform.flipped; Rotation = STransform.Rotation }

    let newComponent = { comp with X = newTopLeft.X; Y = newTopLeft.Y }

    { sym with
        Component = newComponent
        PortMaps = { Order = portOrder; Orientation = portOrientation }
        STransform = newSTransform
        LabelHasDefaultPos = true
        Pos = newTopLeft }
    |> calcLabelBoundingBox
    |> (fun sym ->
        match flip with
        | FlipHorizontal -> sym
        | FlipVertical ->
            let newblock = getBlock [ sym ]
            let newblockCenter = newblock.Centre()
            sym
            |> rotateSymbolInBlock Degree270 newblockCenter
            |> rotateSymbolInBlock Degree270 newblockCenter)

/// <summary>HLP 23: AUTHOR Ismagilov - Scales selected symbol up or down.</summary>
/// <param name="scaleType"> Scale up or down. Scaling distance is constant</param>
/// <param name="block"> Bounding box of selected components</param>
/// <param name="sym"> Symbol to be rotated</param>
/// <returns>New symbol after scaled about block centre.</returns>
let scaleSymbolInBlock
    //(Mag: float)
    (scaleType: ScaleType)
    (block: BoundingBox)
    (sym: Symbol)
    : Symbol
    =

    let symCenter = getRotatedSymbolCentre sym

    //Get x and y proportion of symbol to block
    let xProp, yProp =
        (symCenter.X - block.TopLeft.X) / block.W, (symCenter.Y - block.TopLeft.Y) / block.H

    let newCenter =
        match scaleType with
        | ScaleUp ->
            { X = (block.TopLeft.X - 5.) + ((block.W + 10.) * xProp)
              Y = (block.TopLeft.Y - 5.) + ((block.H + 10.) * yProp) }
        | ScaleDown ->
            { X = (block.TopLeft.X + 5.) + ((block.W - 10.) * xProp)
              Y = (block.TopLeft.Y + 5.) + ((block.H - 10.) * yProp) }

    let h, w = getRotatedHAndW sym
    let newPos = { X = (newCenter.X) - w / 2.; Y = (newCenter.Y) - h / 2. }
    let newComponent = { sym.Component with X = newPos.X; Y = newPos.Y }

    { sym with Pos = newPos; Component = newComponent; LabelHasDefaultPos = true }

/// HLP 23: AUTHOR Klapper - Rotates a symbol based on a degree, including: ports and component parameters.

let rotateSymbolByDegree (degree: Rotation) (sym: Symbol) =
    let pos =
        { X = sym.Component.X + sym.Component.W / 2.0
          Y = sym.Component.Y + sym.Component.H / 2.0 }
    match degree with
    | Degree0 -> sym
    | _ -> rotateSymbolInBlock degree pos sym

/// <summary>HLP 23: AUTHOR Ismagilov - Rotates a block of symbols, returning the new symbol model</summary>
/// <param name="compList"> List of ComponentId's of selected components</param>
/// <param name="model"> Current symbol model</param>
/// <param name="rotation"> Type of rotation to do</param>
/// <returns>New rotated symbol model</returns>
let rotateBlock (compList: ComponentId list) (model: SymbolT.Model) (rotation: Rotation) =

    printfn "running rotateBlock"
    let SelectedSymbols = List.map (fun x -> model.Symbols |> Map.find x) compList
    let UnselectedSymbols =
        model.Symbols
        |> Map.filter (fun x _ -> not (List.contains x compList))

    //Get block properties of selected symbols
    let block = getBlock SelectedSymbols

    //Rotated symbols about the center
    let newSymbols =
        List.map (fun x -> rotateSymbolInBlock (invertRotation rotation) (block.Centre()) x) SelectedSymbols

    //return model with block of rotated selected symbols, and unselected symbols
    { model with
        Symbols =
            ((Map.ofList (List.map2 (fun x y -> (x, y)) compList newSymbols)
              |> Map.fold (fun acc k v -> Map.add k v acc) UnselectedSymbols)) }

let oneCompBoundsBothEdges (selectedSymbols: Symbol list) =
    let maxXSymCentre =
        selectedSymbols
        |> List.maxBy (fun (x: Symbol) -> x.Pos.X + snd (getRotatedHAndW x))
        |> getRotatedSymbolCentre
    let minXSymCentre =
        selectedSymbols
        |> List.minBy (fun (x: Symbol) -> x.Pos.X)
        |> getRotatedSymbolCentre
    let maxYSymCentre =
        selectedSymbols
        |> List.maxBy (fun (y: Symbol) -> y.Pos.Y + fst (getRotatedHAndW y))
        |> getRotatedSymbolCentre
    let minYSymCentre =
        selectedSymbols
        |> List.minBy (fun (y: Symbol) -> y.Pos.Y)
        |> getRotatedSymbolCentre
    (maxXSymCentre.X = minXSymCentre.X)
    || (maxYSymCentre.Y = minYSymCentre.Y)

let findSelectedSymbols (compList: ComponentId list) (model: SymbolT.Model) =
    List.map (fun x -> model.Symbols |> Map.find x) compList

let getScalingFactorAndOffsetCentre (min: float) (matchMin: float) (max: float) (matchMax: float) =
    let scaleFact =
        if min = max || matchMax <= matchMin then
            1.
        else
            (matchMin - matchMax) / (min - max)
    let offsetC =
        if scaleFact = 1. then
            0.
        else
            (matchMin - min * scaleFact) / (1. - scaleFact)
    (scaleFact, offsetC)

let getScalingFactorAndOffsetCentreGroup
    (matchBBMin: XYPos)
    (matchBBMax: XYPos)
    (selectedSymbols: Symbol list)
    : ((float * float) * (float * float))
    =
    //(compList: ComponentId list)
    //(model: SymbolT.Model)

    //let selectedSymbols = List.map (fun x -> model.Symbols |> Map.find x) compList

    let maxXSym =
        selectedSymbols
        |> List.maxBy (fun (x: Symbol) -> x.Pos.X + snd (getRotatedHAndW x))

    let oldMaxX = (maxXSym |> getRotatedSymbolCentre).X
    let newMaxX =
        matchBBMax.X
        - (snd (getRotatedHAndW maxXSym)) / 2.

    let minXSym =
        selectedSymbols
        |> List.minBy (fun (x: Symbol) -> x.Pos.X)

    let oldMinX = (minXSym |> getRotatedSymbolCentre).X
    let newMinX =
        matchBBMin.X
        + (snd (getRotatedHAndW minXSym)) / 2.

    let maxYSym =
        selectedSymbols
        |> List.maxBy (fun (y: Symbol) -> y.Pos.Y + fst (getRotatedHAndW y))

    let oldMaxY = (maxYSym |> getRotatedSymbolCentre).Y
    let newMaxY =
        matchBBMax.Y
        - (fst (getRotatedHAndW maxYSym)) / 2.

    let minYSym =
        selectedSymbols
        |> List.minBy (fun (y: Symbol) -> y.Pos.Y)

    let oldMinY = (minYSym |> getRotatedSymbolCentre).Y
    let newMinY =
        matchBBMin.Y
        + (fst (getRotatedHAndW minYSym)) / 2.

    let xSC = getScalingFactorAndOffsetCentre oldMinX newMinX oldMaxX newMaxX
    let ySC = getScalingFactorAndOffsetCentre oldMinY newMinY oldMaxY newMaxY
    // printfn "oneCompBoundsBothEdges:%A" oneCompBoundsBothEdges
    // printfn "Max: OriginalX: %A" oldMaxX
    // printfn "Max: XMatch: %A" newMaxX
    // printfn "Min: OriginalX: %A" oldMinX
    // printfn "Min: XMatch: %A" newMinX
    // printfn "scaleFact: %A" (fst xSC)
    // printfn "scaleC: %A" (snd xSC)
    // printfn "Max: GotX: %A" (((oldMaxX- (snd xSC)) * (fst xSC)) + (snd xSC))
    // printfn "Min: GotX: %A" (((oldMinX - (snd xSC)) * (fst xSC)) + (snd xSC))
    // printfn "Max: OriginalY: %A" oldMaxY
    // printfn "Max: YMatch: %A" newMaxY
    // printfn "Min: OriginalY: %A" oldMinY
    // printfn "Min: YMatch: %A" newMinY
    // printfn "scaleFact: %A" (fst ySC)
    // printfn "scaleC: %A" (snd ySC)
    // printfn "Max: GotY: %A" (((oldMaxY- (snd ySC)) * (fst ySC)) + (snd ySC))
    // printfn "Min: GotY: %A" (((oldMinY - (snd ySC)) * (fst ySC)) + (snd ySC))
    (xSC, ySC)

let scaleSymbol xYSC sym =

    let symCentre = getRotatedSymbolCentre sym
    let translateFunc scaleFact offsetC coordinate =
        (coordinate - offsetC) * scaleFact + offsetC
    let xSC = fst xYSC
    let ySC = snd xYSC
    let newX = translateFunc (fst xSC) (snd xSC) symCentre.X
    let newY = translateFunc (fst ySC) (snd ySC) symCentre.Y

    let symCentreOffsetFromTopLeft =
        { X = (snd (getRotatedHAndW sym)) / 2.; Y = (fst (getRotatedHAndW sym)) / 2. }
    let newTopLeftPos =
        { X = newX; Y = newY }
        - symCentreOffsetFromTopLeft
    let newComp = { sym.Component with X = newTopLeftPos.X; Y = newTopLeftPos.Y }

    { sym with Pos = newTopLeftPos; Component = newComp; LabelHasDefaultPos = true }

/// <summary>
/// Modifies a group of selected symbols in the model using a given function.
/// </summary>
/// <param name="compList">A list of component IDs for the symbols to be modified.</param>
/// <param name="model">The current model containing all symbols.</param>
/// <param name="selectedSymbols">A list of the symbols to be modified.</param>
/// <param name="modifySymbolFunc">A function that takes a symbol and returns a modified symbol.</param>
/// <returns>A new model with the selected symbols modified.</returns>
/// CHANGES:
/// Simplified to a single pipeline that checks if the component ID is in compList, then modifies accordingly
/// Before this, two maps were created, one with selected symbols and one with unselected symbols,
/// then folded together. This was quite cumbersome.
/// XML comments were also added to this function.
let groupNewSelectedSymsModel
    (compList: ComponentId list)
    (model: SymbolT.Model)
    (selectedSymbols: Symbol list)
    (modifySymbolFunc)
    =
    // Run through a single pipeline that checks if the component ID is in compList.
    // If it is, apply the modifySymbolFunc to it. If it's not, keep it the same.
    let newSymbols =
        model.Symbols
        |> Map.map (fun k v ->
            if (List.contains k compList) then
                (modifySymbolFunc v)
            else
                v)

    { model with Symbols = newSymbols }

/// <summary>HLP 23: AUTHOR Ismagilov - Flips a block of symbols, returning the new symbol model</summary>
/// <param name="compList"> List of ComponentId's of selected components</param>
/// <param name="model"> Current symbol model</param>
/// <param name="flip"> Type of flip to do</param>
/// <returns>New flipped symbol model</returns>
/// Simplified to a single pipeline that checks if the component ID is in compList, then modifies accordingly
/// Before this, two maps were created, one with selected symbols and one with unselected symbols,
/// then folded together. This was quite cumbersome.
let flipBlock (compList: ComponentId list) (model: SymbolT.Model) (flip: FlipType) =
    // Get the block of the selected symbols.
    let block = getBlock (List.map (fun x -> model.Symbols |> Map.find x) compList)

    // Run through a single pipeline that checks if the component ID is in compList.
    // If it is, flip the symbol. If it's not, keep it the same.
    let newSymbols =
        model.Symbols
        |> Map.map (fun k v ->
            if List.contains k compList then
                flipSymbolInBlock flip (block.Centre()) v
            else
                v)

    { model with Symbols = newSymbols }

let postUpdateScalingBox (model: SheetT.Model, cmd) =

    let symbolCmd (msg: SymbolT.Msg) =
        Elmish.Cmd.ofMsg (ModelType.Msg.Sheet(SheetT.Wire(BusWireT.Symbol msg)))
    let sheetCmd (msg: SheetT.Msg) =
        Elmish.Cmd.ofMsg (ModelType.Msg.Sheet msg)

    if (model.SelectedComponents.Length < 2) then
        match model.ScalingBox with
        | None -> model, cmd
        | _ ->
            { model with ScalingBox = None },
            [ symbolCmd (SymbolT.DeleteSymbols (model.ScalingBox.Value).ButtonList)
              sheetCmd SheetT.UpdateBoundingBoxes ]
            |> List.append [ cmd ]
            |> Elmish.Cmd.batch
    else
        let newBoxBound =
            model.SelectedComponents
            |> List.map (fun id -> Map.find id model.Wire.Symbol.Symbols)
            |> getBlock
        match model.ScalingBox with
        | Some value when value.ScalingBoxBound = newBoxBound -> model, cmd
        | _ ->
            let topleft = newBoxBound.TopLeft
            let rotateDeg90OffSet: XYPos =
                { X = newBoxBound.W + 57.; Y = (newBoxBound.H / 2.) - 12.5 }
            let rotateDeg270OffSet: XYPos = { X = -69.5; Y = (newBoxBound.H / 2.) - 12.5 }
            let buttonOffSet: XYPos = { X = newBoxBound.W + 47.5; Y = -47.5 }
            let dummyPos = (topleft + buttonOffSet)

            let makeButton = SymbolUpdate.createAnnotation ThemeType.Colourful
            let buttonSym =
                { makeButton ScaleButton dummyPos with Pos = (topleft + buttonOffSet) }
            let makeRotateSym sym =
                { sym with Component = { sym.Component with H = 25.; W = 25. } }
            let rotateDeg90Sym =
                makeButton (RotateButton Degree90) (topleft + rotateDeg90OffSet)
                |> makeRotateSym
            let rotateDeg270Sym =
                { makeButton (RotateButton Degree270) (topleft + rotateDeg270OffSet) with
                    SymbolT.STransform = { Rotation = Degree90; flipped = false } }
                |> makeRotateSym

            let newSymbolMap =
                model.Wire.Symbol.Symbols
                |> Map.add buttonSym.Id buttonSym
                |> Map.add rotateDeg270Sym.Id rotateDeg270Sym
                |> Map.add rotateDeg90Sym.Id rotateDeg90Sym
            let initScalingBox: SheetT.ScalingBox =
                { ScalingBoxBound = newBoxBound
                  ScaleButton = buttonSym
                  RotateDeg90Button = rotateDeg90Sym
                  RotateDeg270Button = rotateDeg270Sym
                  ButtonList = [ buttonSym.Id; rotateDeg270Sym.Id; rotateDeg90Sym.Id ] }
            let newCmd =
                match model.ScalingBox with
                | Some _ ->
                    [ symbolCmd (SymbolT.DeleteSymbols (model.ScalingBox.Value).ButtonList)
                      sheetCmd SheetT.UpdateBoundingBoxes ]
                    |> List.append [ cmd ]
                    |> Elmish.Cmd.batch
                | None -> cmd
            model
            |> Optic.set SheetT.scalingBox_ (Some initScalingBox)
            |> Optic.set SheetT.symbols_ newSymbolMap,
            newCmd
