﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinFreeEdgeVariable : GremlinEdgeTableVariable
    {
        public GremlinFreeEdgeVariable(WEdgeType edgeType)
        {
            EdgeType = edgeType;
        }

        internal override void InV(GremlinToSqlContext currentContext)
        {
            GremlinVariable inVertex = currentContext.GetSinkVertex(this);
            if (EdgeType == WEdgeType.BothEdge)
            {
                Populate("_sink");

                GremlinVariableProperty sinkProperty = new GremlinVariableProperty(this, "_sink");
                currentContext.VariableProperties.Add(sinkProperty);
                GremlinBoundVertexVariable outVertex = new GremlinBoundVertexVariable(sinkProperty);
                currentContext.VariableList.Add(outVertex);
                currentContext.TableReferences.Add(outVertex);

                currentContext.PivotVariable = outVertex;
            }
            else
            {
                if (inVertex == null)
                {
                    var path = currentContext.GetPathFromPathList(this);
                    GremlinFreeVertexVariable newVertex = new GremlinFreeVertexVariable();
                    path.SetSinkVariable(newVertex);
                    currentContext.TableReferences.Add(newVertex);
                    currentContext.VariableList.Add(newVertex);
                    currentContext.PivotVariable = newVertex;
                }
                else
                {
                    currentContext.PivotVariable = inVertex;
                }
            }
        }

        internal override void OutV(GremlinToSqlContext currentContext)
        {
            // A naive implementation would be: add a new bound/free vertex to the variable list. 
            // A better implementation should reason the status of the edge variable and only reset
            // the pivot variable if possible, thereby avoiding adding a new vertex variable  
            // and reducing one join.
            if (EdgeType == WEdgeType.BothEdge)
            {
                Populate("_source");

                GremlinVariableProperty sourceProperty = new GremlinVariableProperty(this, "_source");
                currentContext.VariableProperties.Add(sourceProperty);
                GremlinBoundVertexVariable outVertex = new GremlinBoundVertexVariable(sourceProperty);
                currentContext.VariableList.Add(outVertex);
                currentContext.TableReferences.Add(outVertex);

                currentContext.PivotVariable = outVertex;
            }
            else
            {
                GremlinVariable outVertex = currentContext.GetSourceVertex(this);
                if (outVertex == null)
                {
                    var path = currentContext.GetPathFromPathList(this);
                    GremlinFreeVertexVariable newVertex = new GremlinFreeVertexVariable();
                    path.SetSourceVariable(newVertex);
                    currentContext.TableReferences.Add(newVertex);
                    currentContext.VariableList.Add(newVertex);
                    currentContext.PivotVariable = newVertex;
                }
                else
                {
                    currentContext.PivotVariable = outVertex;
                }
            }
        }

        internal override void OtherV(GremlinToSqlContext currentContext)
        {
            if (EdgeType == WEdgeType.BothEdge)
            {
                Populate("_other");

                GremlinVariableProperty otherProperty = new GremlinVariableProperty(this, "_other");
                currentContext.VariableProperties.Add(otherProperty);
                GremlinBoundVertexVariable outVertex = new GremlinBoundVertexVariable(otherProperty);
                currentContext.VariableList.Add(outVertex);
                currentContext.TableReferences.Add(outVertex);

                currentContext.PivotVariable = outVertex;
            }
            else
            {
                var path = currentContext.GetPathFromPathList(this);

                if (path == null)
                {
                    throw new QueryCompilationException("Can't find a path");
                }

                if (EdgeType == WEdgeType.InEdge)
                {
                    OutV(currentContext);
                }
                else if (EdgeType == WEdgeType.OutEdge)
                {
                    InV(currentContext);
                }
                else if (EdgeType == WEdgeType.BothEdge)
                {
                    throw new QueryCompilationException();
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
        }
    }
}
