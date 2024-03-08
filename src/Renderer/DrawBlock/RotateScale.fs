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
    This module contains the code that rotates and scales blocks of components.
    It was collected from HLP work in 2023 and has some technical debt and also unused functions.
    It requires better documentation of teh pasrts now used.
*)

(*ra2520
Most significant improvements:
1. Created a new helper function transformBlock that performs the general operations for both rotateBlock
   and flipBlock, improving code reuse and (possibly?) making future extensions easier.
2. Used transformSelectedSymbols (an alias for groupNewSelectedSymsModel) as the basis of transformBlock,
   reusing this functionality in a way that was not taken advantage of before.
3. Restructured scaleSymbol to improve functional abstraction and allow the use of more pipelines (and fewer
   let statements), making the flow of data clearer.
Honourable mention: input-wrapped xYSC into an anonymous record using pattern matching (3 transforms in 1!).
Note: I moved rotateBlock to my section (although it's not technically assigned to me) so it could take
advantage of my new helper function, transformBlock.
Transformation 1:
- Changed function boundaries in scaleSymbol to allow use of pipelining, break more problems into subproblems
  along meaningful boundaries e.g. 2D as an extension of 1D.
- Created global helper function transformBlock that does most of the work of both rotateBlock and flipBlock.
- Created local helper function removeOldScalingBoxCmd in postUpdateScalingBox to reduce repeated code.
- Used input wrapping to convert the nested tuple in scaleSymbol to an anonymous record, with meaningful
  field names to make later code more readable.
Transformation 2:
- Put model last in transformSelectedSymbols and transformBlock for easier currying (model is likely to be
  changed most often e.g. in pipeline). Note I was quite limited in what I could do as most of my assigned
  functions are top-level functions.
- Made block the first parameter of modifySymbolFunc in transformBlock to allow for currying - the curried
  function can be directly passed to transformSelectedSymbols.
Transformation 3:
- Reorganised scaleSymbol to allow the use of a pipeline to make newTopLeftPos, with an anonymous function
  at the end to reduce unnecessary let statements.
- Used pipelining throughout transformSelectedSymbols, and particularly to generate the new selected symbol
  map.
- Although there were opportunities to make pipelines longer in places, I deliberately separated them along
  meaningful boundaries.
Transformation 4:
- Used an anonymous record for input wrapping of xYSC in scaleSymbol, as fields can be given meaningful names
  and it makes sense to group the paramters together. Anonymous record was used as these parameters are only
  used once.
Transformation 5:
- Used pattern matching to extract and name the relevant elements of the input tuple of scaleSymbol, putting
  them into an anonymous record (better than using fst/snd many times).
Additional discussion (can be skipped):
- I have evaluated postUpdateScalingBox's implementation of its required control flow as being optimal.
  Using an if/else statement handles the inquality better than a match statement. Also, I considered condensing
  the nested control flow into a single match statement - however, for the second match statement in the code,
  both cases use newBoxBound (an example of transformation 1 - match case compression), and condensing
  everything into a single match statement would lose this.
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

/// Type used to record a wire between two symbols.
type WireSymbols =
    { SymA: Symbol
      SymB: Symbol
      Wire: Wire }

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

let getPortAB wModel wireSyms =
    let ports = portsOfWires wModel [ wireSyms.Wire ]
    let portA = filterPortBySym ports wireSyms.SymA |> List.head
    let portB = filterPortBySym ports wireSyms.SymB |> List.head
    portA, portB

/// Try to get two ports that are on opposite edges.
let getOppEdgePortInfo
    (wModel: BusWireT.Model)
    (symbolToSize: Symbol)
    (otherSymbol: Symbol)
    : (PortInfo * PortInfo) option =
    let wires = wiresBtwnSyms wModel symbolToSize otherSymbol

    let tryGetOppEdgePorts wireSyms =
        let portA, portB = getPortAB wModel wireSyms
        let edgeA = getSymbolPortOrientation wireSyms.SymA portA
        let edgeB = getSymbolPortOrientation wireSyms.SymB portB

        match edgeA = edgeB.Opposite with
        | true -> Some(makePortInfo wireSyms.SymA portA, makePortInfo wireSyms.SymB portB)
        | _ -> None

    wires
    |> List.tryPick (fun w ->
        tryGetOppEdgePorts
            { SymA = symbolToSize
              SymB = otherSymbol
              Wire = w })

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

let alignSymbols
    (wModel: BusWireT.Model)
    (symbolToSize: Symbol)
    (otherSymbol: Symbol)
    : BusWireT.Model =

    // Only attempt to align symbols if they are connected by ports on parallel edges.
    match getOppEdgePortInfo (wModel:BusWireT.Model) symbolToSize otherSymbol with
    | None -> wModel
    | Some(movePortInfo, otherPortInfo) ->
        let offset = alignPortsOffset movePortInfo otherPortInfo
        let symbol' = moveSymbol offset symbolToSize
        let model' = Optic.set (symbolOf_ symbolToSize.Id) symbol' wModel
        BusWireSeparate.routeAndSeparateSymbolWires model' symbolToSize.Id

/// HLP23: To test this, it must be given two symbols interconnected by wires. It then resizes symbolToSize
/// so that the connecting wires are exactly straight
/// HLP23: It should work out the interconnecting wires (wires) from
/// the two symbols, wModel.Wires and sModel.Ports
/// It will do nothing if symbolToOrder is not a Custom component (which has adjustable size).
let reSizeSymbol (wModel: BusWireT.Model) (symbolToSize: Symbol) (otherSymbol: Symbol) : (Symbol) =
    let wires = wiresBtwnSyms wModel symbolToSize otherSymbol

    // Try to get two ports that are on opposite edges, if none found just use any two ports.
    let resizePortInfo, otherPortInfo =
        match getOppEdgePortInfo wModel symbolToSize otherSymbol with
        | None ->
            let pA, pB = getPortAB wModel { SymA = symbolToSize; SymB = otherSymbol; Wire = wires[0] }
            makePortInfo symbolToSize pA, makePortInfo symbolToSize pB
        | Some(pIA, pIB) -> (pIA, pIB)

    let h, w =
        match resizePortInfo.side with
        | Left | Right ->
            otherPortInfo.portGap * (resizePortInfo.portDimension + 2.0 * resizePortInfo.gap), resizePortInfo.w
        | Top | Bottom ->
            resizePortInfo.h, otherPortInfo.portGap * (resizePortInfo.portDimension + 2.0 * resizePortInfo.topBottomGap)

    match symbolToSize.Component.Type with
    | Custom _ ->
        let scaledSymbol = setCustomCompHW h w symbolToSize
        let scaledInfo = makePortInfo scaledSymbol resizePortInfo.port
        let offset = alignPortsOffset scaledInfo otherPortInfo
        moveSymbol offset scaledSymbol
    | _ ->
        symbolToSize

/// For UI to call ResizeSymbol.
let reSizeSymbolTopLevel
    (wModel: BusWireT.Model)
    (symbolToSize: Symbol)
    (otherSymbol: Symbol)
    : BusWireT.Model =
    printfn $"ReSizeSymbol: ToResize:{symbolToSize.Component.Label}, Other:{otherSymbol.Component.Label}"

    let scaledSymbol = reSizeSymbol wModel symbolToSize otherSymbol

    let model' = Optic.set (symbolOf_ symbolToSize.Id) scaledSymbol wModel
    BusWireSeparate.routeAndSeparateSymbolWires model' symbolToSize.Id

/// For each edge of the symbol, store a count of how many connections it has to other symbols.
type SymConnDataT =
    { ConnMap: Map<ComponentId * Edge, int> }

/// If a wire between a target symbol and another symbol connects opposite edges, return the edge that the wire is connected to on the target symbol 
let tryWireSymOppEdge (wModel: Model) (wire: Wire) (sym: Symbol) (otherSym: Symbol) =
    let symEdge = wireSymEdge wModel wire sym
    let otherSymEdge = wireSymEdge wModel wire otherSym

    match symEdge = otherSymEdge.Opposite with
    | true -> Some symEdge
    | _ -> None

let updateOrInsert (symConnData: SymConnDataT) (edge: Edge) (cid: ComponentId) =
    let m = symConnData.ConnMap
    let count = Map.tryFind (cid, edge) m |> Option.defaultValue 0 |> (+) 1
    { ConnMap = Map.add (cid, edge) count m }

// TODO: this is copied from Sheet.notIntersectingComponents. It requires SheetT.Model, which is not accessible from here. Maybe refactor it.
let noSymbolOverlap (boxesIntersect: BoundingBox -> BoundingBox -> bool) boundingBoxes sym =
    let symBB = getSymbolBoundingBox sym

    boundingBoxes
    |> Map.filter (fun sId boundingBox -> boxesIntersect boundingBox symBB && sym.Id <> sId)
    |> Map.isEmpty

/// Finds the optimal size and position for the selected symbol w.r.t. to its surrounding symbols.
let optimiseSymbol
    (wModel: BusWireT.Model)
    (symbol: Symbol)
    (boundingBoxes: Map<CommonTypes.ComponentId, BoundingBox>)
    : BusWireT.Model =

    // If a wire connects the target symbol to another symbol, note which edge it is connected to
    let updateData (symConnData: SymConnDataT) _ (wire: Wire) =
        let symS, symT = getSourceSymbol wModel wire, getTargetSymbol wModel wire

        let otherSymbol =
            match symS, symT with
            | _ when (symS.Id <> symbol.Id) && (symT.Id = symbol.Id) -> Some symS
            | _ when (symS = symbol) && (symT <> symbol) -> Some symT
            | _ -> None

        match otherSymbol with
        | Some otherSym ->
            let edge = tryWireSymOppEdge wModel wire symbol otherSym

            match edge with
            | Some e -> updateOrInsert symConnData e otherSym.Id
            | None -> symConnData // should not happen
        | None -> symConnData 

    // Look through all wires to build up SymConnDataT.
    let symConnData = ({ ConnMap = Map.empty }, wModel.Wires) ||> Map.fold updateData

    let tryResize (symCount: ((ComponentId * Edge) * int) array) sym =

        let alignSym (sym: Symbol) (otherSym: Symbol) =
            let resizedSym = reSizeSymbol wModel sym otherSym
            let noOverlap = noSymbolOverlap DrawHelpers.boxesIntersect boundingBoxes resizedSym

            match noOverlap with
            | true -> true, resizedSym
            | _ -> false, sym

        let folder (hAligned, vAligned, sym) ((cid, edge), _) =
            let otherSym = Optic.get (symbolOf_ cid) wModel       

            match hAligned, vAligned with
            | false, _ when edge = Top || edge = Bottom ->
                let hAligned', resizedSym = alignSym sym otherSym
                (hAligned', vAligned, resizedSym)
            | _, false when edge = Left || edge = Right ->
                let vAligned', resizedSym = alignSym sym otherSym
                (hAligned, vAligned', resizedSym)
            | _ -> (hAligned, vAligned, sym)

        let (_, _, sym') = ((false, false, sym), symCount) ||> Array.fold folder
        sym'

    let scaledSymbol =
        let symCount =
            Map.toArray symConnData.ConnMap
            |> Array.filter (fun (_, count) -> count > 1)
            |> Array.sortByDescending snd

        tryResize symCount symbol

    let model' = Optic.set (symbolOf_ symbol.Id) scaledSymbol wModel
    BusWireSeparate.routeAndSeparateSymbolWires model' symbol.Id


(* HLP24-Cleanup (pw321):
    The previous implementation had a whole jumble of functions for each min or max of the bounding box, for each of the X or Y directions.
    I chose to just rewrite it because there were too many new variable definitions and too many anonymous functions for no reason, lots of unnecessary bracketing, and wayyy too much code repetition.

    The new implementation uses a single pipeline to:
    - get the bounding boxes of all symbols
    - split into TopLeft and BottomRight
    - split into X and Y coords for each
    - concatenating all the X and Y coords into their respective list
        - admittedly a little messy, but readable
    - get the mins and maxes of all the values symbols in the block
      for both the X or Y directions
    - before then formatting them in a Bounding Box, as before :)
*)


/// <summary>HLP 23: AUTHOR Ismagilov - Get the bounding box of multiple selected symbols\
/// HLP24: modified by Pierce Wiegerling</summary>
/// <param name="symbols"> Selected symbols list</param>
/// <returns>Bounding Box</returns>
let getBlock (symbols:Symbol List) :BoundingBox = 
    let forEach (f: 'a -> 'b) (a: 'a, b: 'a) = (f a, f b)
    
    symbols
    |> List.map getSymbolBoundingBox
    |> List.map (fun bb -> (bb.TopLeft, bb.BottomRight()))
    |> List.map (fun (tlVal, brVal) -> 
        [tlVal.X; brVal.X], [tlVal.Y; brVal.Y]  // split into X, Y components
    )
    |> List.unzip  // separate into X and Y lists
    |> forEach (List.collect id)  // collecting all the BB sublists
    |> fun (xList, yList) -> 
        let minX = List.min xList
        let minY = List.min yList
        let maxX = List.max xList
        let maxY = List.max yList

        {TopLeft = {X = minX; Y = minY}; W = maxX-minX; H = maxY-minY}


(* HLP24-Cleanup (pw321):
    No major changes, just tidied up some type annotations, quite liked the symmetry of the functions
*)


/// <summary>HLP 23: AUTHOR Ismagilov - Takes a point Pos, a centre Pos, and a rotation type and returns the point flipped about the centre</summary>
/// <param name="point"> Original XYPos</param>
/// <param name="center"> The center XYPos that the point is rotated about</param>
/// <param name="rotation"> Clockwise or Anticlockwise </param>
/// <returns>New flipped point</returns>
let rotatePointAboutBlockCentre 
    (point: XYPos) 
    (centre: XYPos) 
    (rotation: Rotation) = 
    let relativeToCentre = (fun x-> x - centre)
    let rotateAboutCentre pointIn = 
        match rotation with 
        | Degree0 -> 
            pointIn
        | Degree90 ->
            { X = pointIn.Y ; Y = -pointIn.X }
        | Degree180 -> 
            { X = -pointIn.X ; Y = -pointIn.Y }
        | Degree270 ->
            { X = -pointIn.Y ; Y = pointIn.X }
           
    let relativeToTopLeft = (fun x-> centre - x)

    point
    |> relativeToCentre
    |> rotateAboutCentre
    |> relativeToTopLeft



(* HLP24-Cleanup (pw321):
    Nothing to see here, nothing needed to be done :)
*)


/// <summary>HLP 23: AUTHOR Ismagilov - Takes a point Pos, a centre Pos, and a flip type and returns the point flipped about the centre</summary>
/// <param name="point"> Original XYPos</param>
/// <param name="center"> The center XYPos that the point is flipped about</param>
/// <param name="flip"> Horizontal or Vertical flip</param>
/// <returns>New flipped point</returns>
let flipPointAboutBlockCentre 
    (point: XYPos)
    (center: XYPos)
    (flip: FlipType) = 
    match flip with
    | FlipHorizontal-> 
        { X = center.X - (point.X - center.X); Y = point.Y } 
    | FlipVertical -> 
        { X = point.X; Y = center.Y - (point.Y - center.Y) }


(* HLP24-Cleanup (pw321):
    just some unnecessary float casting (h and w are already floats)

    Just tidied up a bit, no big changes
*)


/// <summary>HLP 23: AUTHOR Ismagilov - Get the new top left of a symbol after it has been rotated</summary>
/// <param name="rotation"> Rotated CW or AntiCW</param>
/// <param name="h"> Original height of symbol (Before rotation)</param>
/// <param name="w"> Original width of symbol (Before rotation)</param>
/// <param name="sym"> Symbol</param>
/// <returns>New top left point of the symbol</returns>
let adjustPosForBlockRotation
        (rotation: Rotation) 
        (h: float) (w: float)
        (pos: XYPos)
         : XYPos =
    let posOffset =
        match rotation with
        | Degree0 -> { X = 0.0; Y = 0.0 }
        | Degree90 -> { X = h ; Y = 0.0 }
        | Degree180 -> { X = w; Y= -h }
        | Degree270 -> { X = 0.0 ; Y = w }
    pos - posOffset


(* HLP24-Cleanup (pw321):
    just some unnecessary float casting (h and w are already floats)

    Just tidied up a bit, no big changes
*)


/// <summary>HLP 23: AUTHOR Ismagilov - Get the new top left of a symbol after it has been flipped</summary>
/// <param name="flip">  Flipped horizontally or vertically</param>
/// <param name="h"> Original height of symbol (Before flip)</param>
/// <param name="w"> Original width of symbol (Before flip)</param>
/// <param name="sym"> Symbol</param>
/// <returns>New top left point of the symbol</returns>
let adjustPosForBlockFlip
        (flip: FlipType) 
        (h: float) (w: float)
        (pos: XYPos) =
    let posOffset =
        match flip with
        | FlipHorizontal -> { X = w ; Y = 0.0 }
        | FlipVertical -> { X = 0.0 ;Y = h }
    pos - posOffset

/// <summary>HLP 23: AUTHOR Ismagilov - Rotate a symbol in its block.</summary>
/// <param name="rotation">  Clockwise or Anticlockwise rotation</param>
/// <param name="block"> Bounding box of selected components</param>
/// <param name="sym"> Symbol to be rotated</param>
/// <returns>New symbol after rotated about block centre.</returns>
let rotateSymbolInBlock 
        (rotation: Rotation) 
        (blockCentre: XYPos)
        (sym: Symbol)  : Symbol =
      
    let h,w = getRotatedHAndW sym

    let newTopLeft = 
        rotatePointAboutBlockCentre sym.Pos blockCentre (invertRotation rotation)
        |> adjustPosForBlockRotation (invertRotation rotation) h w

    let newComponent = { sym.Component with X = newTopLeft.X; Y = newTopLeft.Y}
    
    let newSTransform = 
        match sym.STransform.Flipped with
        | true -> 
            {sym.STransform with Rotation = combineRotation (invertRotation rotation) sym.STransform.Rotation}  
        | _-> 
            {sym.STransform with Rotation = combineRotation rotation sym.STransform.Rotation}

    { sym with 
        Pos = newTopLeft;
        PortMaps = rotatePortInfo rotation sym.PortMaps
        STransform = newSTransform 
        LabelHasDefaultPos = true
        Component = newComponent
    } |> calcLabelBoundingBox 


/// <summary>HLP 23: AUTHOR Ismagilov - Flip a symbol horizontally or vertically in its block.</summary>
/// <param name="flip">  Flip horizontally or vertically</param>
/// <param name="block"> Bounding box of selected components</param>
/// <param name="sym"> Symbol to be flipped</param>
/// <returns>New symbol after flipped about block centre.</returns>
let flipSymbolInBlock
    (flip: FlipType)
    (blockCentre: XYPos)
    (sym: Symbol) : Symbol =

    let h,w = getRotatedHAndW sym
    //Needed as new symbols and their components need their Pos updated (not done in regular flip symbol)
    let newTopLeft = 
        flipPointAboutBlockCentre sym.Pos blockCentre flip
        |> adjustPosForBlockFlip flip h w

    let portOrientation = 
        sym.PortMaps.Orientation |> Map.map (fun id side -> flipSideHorizontal side)

    let flipPortList currPortOrder side =
        currPortOrder |> Map.add (flipSideHorizontal side ) sym.PortMaps.Order[side]

    let portOrder = 
        (Map.empty, [Edge.Top; Edge.Left; Edge.Bottom; Edge.Right]) ||> List.fold flipPortList
        |> Map.map (fun edge order -> List.rev order)       

    let newSTransform = 
        {Flipped= not sym.STransform.Flipped;
        Rotation= sym.STransform.Rotation} 

    let newcomponent = {sym.Component with X = newTopLeft.X; Y = newTopLeft.Y}

    { sym with
        Component = newcomponent
        PortMaps = {Order=portOrder;Orientation=portOrientation}
        STransform = newSTransform
        LabelHasDefaultPos = true
        Pos = newTopLeft
    }
    |> calcLabelBoundingBox
    |> (fun sym -> 
        match flip with
        | FlipHorizontal -> sym
        | FlipVertical -> 
            let newblock = getBlock [sym]
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
    (sym: Symbol) : Symbol =

    let symCenter = getRotatedSymbolCentre sym

    //Get x and y proportion of symbol to block
    let xProp, yProp = (symCenter.X - block.TopLeft.X) / block.W, (symCenter.Y - block.TopLeft.Y) / block.H

    let newCenter = 
        match scaleType with
            | ScaleUp ->
                {X = (block.TopLeft.X-5.) + ((block.W+10.) * xProp); Y = (block.TopLeft.Y-5.) + ((block.H+10.) * yProp)}
            | ScaleDown ->
                {X= (block.TopLeft.X+5.) + ((block.W-10.) * xProp); Y=  (block.TopLeft.Y+5.) + ((block.H-10.) * yProp)}

    let h,w = getRotatedHAndW sym
    let newPos = {X = (newCenter.X) - w/2.; Y= (newCenter.Y) - h/2.}
    let newComponent = { sym.Component with X = newPos.X; Y = newPos.Y}

    {sym with Pos = newPos; Component=newComponent; LabelHasDefaultPos=true}


/// HLP 23: AUTHOR Klapper - Rotates a symbol based on a degree, including: ports and component parameters.

let rotateSymbolByDegree (degree: Rotation) (sym:Symbol)  =
    let pos = {X = sym.Component.X + sym.Component.W / 2.0 ; Y = sym.Component.Y + sym.Component.H / 2.0 }
    match degree with
    | Degree0 -> sym
    | _ ->  rotateSymbolInBlock degree pos sym

//ra2520 removed rotateBlock to incorporate a helper function later.

let oneCompBoundsBothEdges (selectedSymbols: Symbol list) = 
    let maxXSymCentre = 
            selectedSymbols
            |> List.maxBy (fun (x:Symbol) -> x.Pos.X + snd (getRotatedHAndW x)) 
            |> getRotatedSymbolCentre
    let minXSymCentre =
            selectedSymbols
            |> List.minBy (fun (x:Symbol) -> x.Pos.X)
            |> getRotatedSymbolCentre
    let maxYSymCentre = 
            selectedSymbols
            |> List.maxBy (fun (y:Symbol) -> y.Pos.Y+ fst (getRotatedHAndW y))
            |> getRotatedSymbolCentre
    let minYSymCentre =
            selectedSymbols
            |> List.minBy (fun (y:Symbol) -> y.Pos.Y)
            |> getRotatedSymbolCentre
    (maxXSymCentre.X = minXSymCentre.X) || (maxYSymCentre.Y = minYSymCentre.Y)
    

let findSelectedSymbols (compList: ComponentId list) (model: SymbolT.Model) = 
    List.map (fun x -> model.Symbols |> Map.find x) compList

let getScalingFactorAndOffsetCentre (min:float) (matchMin:float) (max:float) (matchMax:float) = 
    let scaleFact = 
        if min = max || matchMax <= matchMin then 1. 
        else (matchMin - matchMax) / (min - max)
    let offsetC = 
        if scaleFact = 1. then 0.
        else (matchMin - min * scaleFact) / (1.-scaleFact)
    (scaleFact, offsetC)

/// Return set of floats that define how a group of components is scaled
let getScalingFactorAndOffsetCentreGroup
    (matchBBMin:XYPos)
    (matchBBMax:XYPos)
    (selectedSymbols: Symbol list) : ((float * float) * (float * float)) = 
    //(compList: ComponentId list)
    //(model: SymbolT.Model)

    //let selectedSymbols = List.map (fun x -> model.Symbols |> Map.find x) compList

    let maxXSym = 
            selectedSymbols
            |> List.maxBy (fun (x:Symbol) -> x.Pos.X + snd (getRotatedHAndW x)) 

    let oldMaxX = (maxXSym |> getRotatedSymbolCentre).X
    let newMaxX = matchBBMax.X - (snd (getRotatedHAndW maxXSym))/2.

    let minXSym =
            selectedSymbols
            |> List.minBy (fun (x:Symbol) -> x.Pos.X)

    let oldMinX = (minXSym |> getRotatedSymbolCentre).X
    let newMinX = matchBBMin.X + (snd (getRotatedHAndW minXSym))/2.
    
    let maxYSym = 
            selectedSymbols
            |> List.maxBy (fun (y:Symbol) -> y.Pos.Y+ fst (getRotatedHAndW y))

    let oldMaxY = (maxYSym |> getRotatedSymbolCentre).Y
    let newMaxY = matchBBMax.Y - (fst (getRotatedHAndW maxYSym))/2.

    let minYSym =
            selectedSymbols
            |> List.minBy (fun (y:Symbol) -> y.Pos.Y)

    let oldMinY = (minYSym |>  getRotatedSymbolCentre).Y
    let newMinY = matchBBMin.Y + (fst (getRotatedHAndW minYSym))/2.
    
    let xSC = getScalingFactorAndOffsetCentre oldMinX newMinX oldMaxX newMaxX
    let ySC = getScalingFactorAndOffsetCentre oldMinY newMinY oldMaxY newMaxY
    (xSC, ySC)

// Changes begin here (also rotateBlock from earlier moved here).

/// Alter position of one symbol as needed in a scaling operation.
/// xYSC has meaning ((XScale, XOffset), (YScale, YOffset)).
let scaleSymbol
        (xYSC: (float * float) * (float * float))
        (sym: Symbol)
        : Symbol = 

    //ra2520 (1/4/5) input wrapping + anon record + pattern matching
    let transformInfo =
        match xYSC with
        | ((xScale, xOffset), (yScale, yOffset)) -> {|XScale = xScale; XOffset = xOffset;
                                                      YScale = yScale; YOffset = yOffset|}

    //ra2520 (1) structural abstraction
    let translate1D scale offset coord = (coord - offset) * scale + offset
    let translate2D (pos: XYPos) : XYPos =
        {X = translate1D transformInfo.XScale transformInfo.XOffset pos.X;
         Y = translate1D transformInfo.YScale transformInfo.YOffset pos.Y}
    
    let halfDims =
        getRotatedHAndW sym
        |> fun (h, w) -> {X = w/2.; Y = h/2.}

    //ra2520 (3) pipelining
    let newTopLeftPos =
        getRotatedSymbolCentre sym
        |> translate2D
        |> fun pos -> pos - halfDims

    let newComponent = {sym.Component with X = newTopLeftPos.X; Y = newTopLeftPos.Y}

    {sym with Pos = newTopLeftPos; Component = newComponent; LabelHasDefaultPos = true}

/// Apply a function to the selected symbols, and return the new model.
/// compList and selectedSymbols refer to pairs of matching component ids and symbols.
/// modifySymbolFunc is applied to each selected symbol to get a new symbol.
//ra2520 (1/2/3) helper function + currying + pipelines used throughout
let transformSelectedSymbols
        (compList: ComponentId list) 
        (selectedSymbols: Symbol list)
        (modifySymbolFunc: Symbol -> Symbol)
        (model: SymbolT.Model) 
        : SymbolT.Model =

    // Zip together the existing component id list and new symbol list.
    let zipSymbols (newSymbols: Symbol list) : Map<ComponentId, Symbol> =
        List.map2 (fun x y -> (x, y)) compList newSymbols
        |> Map.ofList

    let newSelectedSymMap =
        selectedSymbols
        |> List.map modifySymbolFunc
        |> zipSymbols

    let unselectedSymMap =
        model.Symbols
        |> Map.filter (fun x _ -> not <| List.contains x compList)

    let addMaps map1 map2 =
        Map.fold (fun acc k v -> Map.add k v acc) map1 map2

    {model with Symbols = addMaps unselectedSymMap newSelectedSymMap}

/// An alias for transformSelectedSymbols, since this function is used by other modules.
/// Apply a function to the selected symbols, and return the new model.
/// compList and selectedSymbols refer to pairs of matching component ids and symbols.
/// modifySymbolFunc is applied to each selected symbol to get a new symbol.
//ra2520 (1) function wrapping (just changing order of parameters)
let groupNewSelectedSymsModel
        (compList: ComponentId list) 
        (model: SymbolT.Model) 
        (selectedSymbols: Symbol list)
        (modifySymbolFunc)
        : SymbolT.Model =
    transformSelectedSymbols compList selectedSymbols modifySymbolFunc model

/// A wrapper for transformSelectedSymbols, specifically for block functions.
/// modifySymbolFunc takes a block and a symbol as input, and returns the transformed symbol.
//ra2520 (1/2) helper function + currying (order of modifySymbolFunc parameters)
let transformBlock
        (compList: ComponentId list)
        (modifySymbolFunc: BoundingBox -> Symbol -> Symbol)
        (model: SymbolT.Model)
        : SymbolT.Model =
    let selectedSymbols = findSelectedSymbols compList model
    let block = getBlock selectedSymbols
    transformSelectedSymbols compList selectedSymbols (modifySymbolFunc block) model

/// Rotates a block of symbols, returning the new symbol model.
/// The selected block is given by compList.
let rotateBlock
        (compList:ComponentId list)
        (model:SymbolT.Model)
        (rotation:Rotation)
        : SymbolT.Model =
    printfn "running rotateBlock"
    let rotateFunc (block: BoundingBox) (symbol: Symbol) : Symbol =
        rotateSymbolInBlock (invertRotation rotation) (block.Centre()) symbol
    transformBlock compList rotateFunc model

/// Flips a block of symbols, returning the new symbol model.
/// The selected block is given by compList.
let flipBlock
        (compList:ComponentId list)
        (model:SymbolT.Model)
        (flip:FlipType)
        : SymbolT.Model =
    let flipFunc (block: BoundingBox) (symbol: Symbol) : Symbol =
        flipSymbolInBlock flip (block.Centre()) symbol
    transformBlock compList flipFunc model

/// After every model update this updates the "scaling box" part of the model to be correctly
/// displayed based on whether multiple components are selected, and if so, what is their "box".
/// In addition to changing the model directly, cmd may contain messages that make further changes.
//ra2520 lots of renaming/reordering things for clarity
let postUpdateScalingBox (model:SheetT.Model, cmd) = 
    
    let symbolCmd (msg: SymbolT.Msg) = Elmish.Cmd.ofMsg (ModelType.Msg.Sheet (SheetT.Wire (BusWireT.Symbol msg)))
    let sheetCmd (msg: SheetT.Msg) = Elmish.Cmd.ofMsg (ModelType.Msg.Sheet msg)
    //ra2520 (1) helper function
    let removeOldScalingBoxCmd (model: SheetT.Model) =
        [model.ScalingBox.Value.ButtonList |> SymbolT.DeleteSymbols |> symbolCmd;
         sheetCmd SheetT.UpdateBoundingBoxes]
        |> List.append [cmd]
        |> Elmish.Cmd.batch

    if model.SelectedComponents.Length <= 1 then 
        match model.ScalingBox with 
        | None -> model, cmd
        | _ -> {model with ScalingBox = None}, removeOldScalingBoxCmd model
    else 
        let newBoxBound = 
            model.SelectedComponents
            |> List.map (fun compId -> Map.find compId model.Wire.Symbol.Symbols)
            |> getBlock
        match model.ScalingBox with 
        | Some value when value.ScalingBoxBound = newBoxBound -> model, cmd
        | _ ->
            //ra2520 renamed/reordered things for clarity
            let topLeft = newBoxBound.TopLeft
            let scaleOffset: XYPos = {X = newBoxBound.W + 47.5; Y = -47.5}
            let rotateDeg90Offset: XYPos = {X = newBoxBound.W+57.; Y = (newBoxBound.H/2.)-12.5}
            let rotateDeg270Offset: XYPos = {X = -69.5; Y = (newBoxBound.H/2.)-12.5}

            let makeButton = SymbolUpdate.createAnnotation ThemeType.Colourful
            let makeRotateSymbol sym = {sym with Component = {sym.Component with H = 25.; W=25.}}

            let scaleButton = {makeButton ScaleButton (topLeft + scaleOffset) with Pos = (topLeft + scaleOffset)}
            let rotateDeg90Button = 
                makeButton (RotateButton Degree90) (topLeft + rotateDeg90Offset)
                |> makeRotateSymbol
            let rotateDeg270Button = 
                {makeButton (RotateButton Degree270) (topLeft + rotateDeg270Offset) 
                    with SymbolT.STransform = {Rotation=Degree90 ; Flipped=false}}
                |> makeRotateSymbol

            let newSymbolMap =
                model.Wire.Symbol.Symbols 
                |> Map.add scaleButton.Id scaleButton 
                |> Map.add rotateDeg270Button.Id rotateDeg270Button 
                |> Map.add rotateDeg90Button.Id rotateDeg90Button
            let initScalingBox: SheetT.ScalingBox = {
                ScalingBoxBound = newBoxBound;
                ScaleButton = scaleButton;
                RotateDeg90Button = rotateDeg90Button;
                RotateDeg270Button = rotateDeg270Button;
                ButtonList = [scaleButton.Id; rotateDeg270Button.Id; rotateDeg90Button.Id];
            }
            let newCmd =
                match model.ScalingBox with
                | Some _ -> removeOldScalingBoxCmd model
                | None -> cmd
            model
            |> Optic.set SheetT.scalingBox_ (Some initScalingBox)
            |> Optic.set SheetT.symbols_ newSymbolMap, 
            newCmd

