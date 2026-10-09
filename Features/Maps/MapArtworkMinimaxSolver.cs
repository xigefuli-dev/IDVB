namespace IDVBuff.Features.Maps;

/// <summary>Maker-time ordinary affine fit minimizing the largest Euclidean pixel residual.</summary>
internal static class MapArtworkMinimaxSolver
{
    private const int VariableCount = 7;
    private const double AccuracyCanonicalPixels = 0.0001;

    internal static double[] Fit(
        int sourceWidth, int sourceHeight, int referenceWidth, int referenceHeight,
        IReadOnlyList<MapArtworkLandmark> landmarks, double[] initialLeastSquaresMatrix)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        ArgumentNullException.ThrowIfNull(initialLeastSquaresMatrix);
        if (sourceWidth <= 0 || sourceHeight <= 0 || referenceWidth <= 0 || referenceHeight <= 0
            || landmarks.Count < 3 || initialLeastSquaresMatrix.Length != 6
            || initialLeastSquaresMatrix.Any(value => !double.IsFinite(value)))
            throw Failure("输入尺寸、对应点或初始 affine 无效");

        var count = landmarks.Count;
        var basis = new double[count, 3];
        var residuals = new double[count, 2];
        var initialMaximum = 0d;
        for (var index = 0; index < count; index++)
        {
            var point = landmarks[index];
            if (point is null || !double.IsFinite(point.SourceX) || !double.IsFinite(point.SourceY)
                || !double.IsFinite(point.ReferenceX) || !double.IsFinite(point.ReferenceY)
                || point.SourceX < 0 || point.SourceX >= sourceWidth
                || point.SourceY < 0 || point.SourceY >= sourceHeight
                || point.ReferenceX < 0 || point.ReferenceX >= referenceWidth
                || point.ReferenceY < 0 || point.ReferenceY >= referenceHeight)
                throw Failure("对应点不在各自楼层的图片内");
            basis[index, 0] = point.SourceX / sourceWidth - 0.5;
            basis[index, 1] = point.SourceY / sourceHeight - 0.5;
            basis[index, 2] = 1;
            residuals[index, 0] = initialLeastSquaresMatrix[0] * point.SourceX
                + initialLeastSquaresMatrix[1] * point.SourceY + initialLeastSquaresMatrix[2] - point.ReferenceX;
            residuals[index, 1] = initialLeastSquaresMatrix[3] * point.SourceX
                + initialLeastSquaresMatrix[4] * point.SourceY + initialLeastSquaresMatrix[5] - point.ReferenceY;
            initialMaximum = Math.Max(initialMaximum, Norm(residuals[index, 0], residuals[index, 1]));
        }
        if (!double.IsFinite(initialMaximum))
            throw Failure("初始 affine 产生了非有限残差");

        // Twice-reorthogonalized QR conditions all six affine variables without changing
        // the affine family. Both residual axes use ONE scale: separate X/Y scales would
        // optimize an ellipse instead of Euclidean distance in canonical pixels.
        var triangular = Orthonormalize(basis);
        // A zero lower bound already proves the requested absolute accuracy in this case.
        if (initialMaximum <= AccuracyCanonicalPixels)
            return (double[])initialLeastSquaresMatrix.Clone();
        var residualScale = Math.Max(1, initialMaximum);
        for (var index = 0; index < count; index++)
            for (var axis = 0; axis < 2; axis++)
                residuals[index, axis] /= residualScale;
        var variables = new double[VariableCount];
        variables[6] = 1 + initialMaximum / residualScale;
        var barrierWeight = variables[6] / (2 * count);

        // SOCP: min radius, ||Q_i * correction + initialResidual_i||_2 <= radius.
        // The cone barrier is -log(radius^2 - residualX^2 - residualY^2).
        // A centered iterate has duality gap 2 * count * barrierWeight. We also
        // construct a projected feasible dual below, so termination does not assume
        // exact centering, depend on the iteration count, or reuse a saved solution.
        for (var outer = 0; outer < 32; outer++)
        {
            var centered = false;
            for (var iteration = 0; iteration < 80; iteration++)
            {
                var lowerBound = DualLowerBound(basis, residuals, variables, out var maximum);
                if ((maximum - lowerBound) * residualScale <= AccuracyCanonicalPixels)
                {
                    var matrix = ComposeMatrix(variables, triangular, initialLeastSquaresMatrix,
                        sourceWidth, sourceHeight, residualScale);
                    // Certify the returned PIXEL matrix as well, including denormalization.
                    var returnedMaximum = MaximumResidual(matrix, landmarks);
                    if (!double.IsFinite(returnedMaximum) || matrix.Any(value => !double.IsFinite(value))
                        || returnedMaximum - lowerBound * residualScale > AccuracyCanonicalPixels)
                        throw Failure("affine 换回像素坐标后未达到数值精度");
                    return matrix;
                }

                var gradient = new double[VariableCount];
                var hessian = new double[VariableCount, VariableCount];
                var objective = EvaluateBarrier(basis, residuals, variables, barrierWeight, gradient, hessian);
                if (!double.IsFinite(objective))
                    throw Failure("迭代离开了可行区域");
                var direction = NewtonDirection(hessian, gradient);
                var slope = 0d;
                for (var column = 0; column < VariableCount; column++)
                    slope += gradient[column] * direction[column];
                if (!double.IsFinite(slope) || slope > 0)
                    throw Failure("Newton 方向无效");
                if (-slope / 2 <= 1e-8)
                {
                    centered = true;
                    break;
                }

                var next = new double[VariableCount];
                var step = 1d;
                var advanced = false;
                for (var backtrack = 0; backtrack < 60; backtrack++)
                {
                    for (var column = 0; column < VariableCount; column++)
                        next[column] = variables[column] + step * direction[column];
                    var nextObjective = EvaluateBarrier(basis, residuals, next, barrierWeight);
                    if (double.IsFinite(nextObjective) && nextObjective <= objective + 0.01 * step * slope)
                    {
                        variables = next;
                        advanced = true;
                        break;
                    }
                    step *= 0.5;
                }
                if (!advanced)
                    throw Failure("线搜索未收敛");
            }
            if (!centered)
                throw Failure("中心迭代未收敛");
            barrierWeight *= 0.2;
        }
        throw Failure("未达到最大残差的数值精度");
    }

    private static double[,] Orthonormalize(double[,] basis)
    {
        var count = basis.GetLength(0);
        var triangular = new double[3, 3];
        for (var column = 0; column < 3; column++)
        {
            for (var pass = 0; pass < 2; pass++)
                for (var previous = 0; previous < column; previous++)
                {
                    var projection = 0d;
                    for (var index = 0; index < count; index++)
                        projection += basis[index, previous] * basis[index, column];
                    triangular[previous, column] += projection;
                    for (var index = 0; index < count; index++)
                        basis[index, column] -= projection * basis[index, previous];
                }
            var squaredNorm = 0d;
            for (var index = 0; index < count; index++)
                squaredNorm += basis[index, column] * basis[index, column];
            var norm = Math.Sqrt(squaredNorm);
            if (!double.IsFinite(norm) || norm <= 1e-12 * Math.Sqrt(count))
                throw Failure("对应点无法确定普通 affine");
            triangular[column, column] = norm;
            for (var index = 0; index < count; index++)
                basis[index, column] /= norm;
        }
        // A Gershgorin bound keeps the smallest singular value above 1/2.
        // It also bounds the effect of the tiny remaining dual projection error.
        for (var row = 0; row < 3; row++)
        {
            var error = 0d;
            for (var column = 0; column < 3; column++)
            {
                var gram = 0d;
                for (var index = 0; index < count; index++)
                    gram += basis[index, row] * basis[index, column];
                error += Math.Abs(gram - (row == column ? 1 : 0));
            }
            if (!double.IsFinite(error) || error >= 0.5)
                throw Failure("对应点的数值归一化退化");
        }
        return triangular;
    }

    private static double EvaluateBarrier(double[,] basis, double[,] residuals, double[] variables,
        double barrierWeight, double[]? gradient = null, double[,]? hessian = null)
    {
        var radius = variables[6];
        if (!double.IsFinite(radius) || radius <= 0)
            return double.PositiveInfinity;
        var objective = radius / barrierWeight;
        if (gradient is not null)
            gradient[6] = 1 / barrierWeight;
        for (var index = 0; index < basis.GetLength(0); index++)
        {
            Residual(basis, residuals, variables, index, out var x, out var y);
            var norm = Norm(x, y);
            // Factoring the slack avoids subtracting almost equal squared radii.
            var slack = (radius - norm) * (radius + norm);
            if (!double.IsFinite(slack) || slack <= 0 || norm >= radius)
                return double.PositiveInfinity;
            objective -= Math.Log(slack);
            if (gradient is null || hessian is null)
                continue;
            var inverse = 1 / slack;
            var xx = 2 * inverse + 4 * x * x * inverse * inverse;
            var yy = 2 * inverse + 4 * y * y * inverse * inverse;
            var xy = 4 * x * y * inverse * inverse;
            var xt = -4 * x * radius * inverse * inverse;
            var yt = -4 * y * radius * inverse * inverse;
            gradient[6] -= 2 * radius * inverse;
            hessian[6, 6] += 2 * (radius * radius + norm * norm) * inverse * inverse;
            for (var row = 0; row < 3; row++)
            {
                var value = basis[index, row];
                gradient[row] += 2 * x * inverse * value;
                gradient[row + 3] += 2 * y * inverse * value;
                hessian[row, 6] += xt * value;
                hessian[6, row] += xt * value;
                hessian[row + 3, 6] += yt * value;
                hessian[6, row + 3] += yt * value;
                for (var column = 0; column < 3; column++)
                {
                    var product = value * basis[index, column];
                    hessian[row, column] += xx * product;
                    hessian[row + 3, column + 3] += yy * product;
                    hessian[row, column + 3] += xy * product;
                    hessian[row + 3, column] += xy * product;
                }
            }
        }
        return objective;
    }

    private static double[] NewtonDirection(double[,] hessian, double[] gradient)
    {
        // Diagonal equilibration before the 7x7 Cholesky solve keeps translation,
        // affine corrections and radius on comparable numerical scales.
        var scales = new double[VariableCount];
        var factor = new double[VariableCount, VariableCount];
        for (var row = 0; row < VariableCount; row++)
        {
            scales[row] = Math.Sqrt(hessian[row, row]);
            if (!double.IsFinite(scales[row]) || scales[row] <= 0)
                throw Failure("Newton 曲率无效");
        }
        for (var row = 0; row < VariableCount; row++)
            for (var column = 0; column <= row; column++)
            {
                var value = hessian[row, column] / scales[row] / scales[column];
                for (var previous = 0; previous < column; previous++)
                    value -= factor[row, previous] * factor[column, previous];
                if (row == column)
                {
                    if (!double.IsFinite(value) || value <= 0)
                        throw Failure("Newton 方程数值退化");
                    factor[row, column] = Math.Sqrt(value);
                }
                else
                    factor[row, column] = value / factor[column, column];
            }
        var direction = new double[VariableCount];
        for (var row = 0; row < VariableCount; row++)
        {
            var value = -gradient[row] / scales[row];
            for (var column = 0; column < row; column++)
                value -= factor[row, column] * direction[column];
            direction[row] = value / factor[row, row];
        }
        for (var row = VariableCount - 1; row >= 0; row--)
        {
            var value = direction[row];
            for (var column = row + 1; column < VariableCount; column++)
                value -= factor[column, row] * direction[column];
            direction[row] = value / factor[row, row];
        }
        for (var row = 0; row < VariableCount; row++)
            direction[row] /= scales[row];
        return direction;
    }

    private static double DualLowerBound(double[,] basis, double[,] residuals, double[] variables,
        out double maximum)
    {
        var count = basis.GetLength(0);
        var dual = new double[count, 2];
        var radius = variables[6];
        maximum = 0;
        var mass = 0d;
        for (var index = 0; index < count; index++)
        {
            Residual(basis, residuals, variables, index, out var x, out var y);
            var norm = Norm(x, y);
            maximum = Math.Max(maximum, norm);
            var slack = (radius - norm) * (radius + norm);
            if (!double.IsFinite(slack) || slack <= 0 || radius <= 0)
                throw Failure("对偶计算遇到非可行迭代");
            dual[index, 0] = x / slack;
            dual[index, 1] = y / slack;
            mass += Norm(dual[index, 0], dual[index, 1]);
        }
        if (!double.IsFinite(mass) || mass <= 0)
            throw Failure("对偶权重无效");
        for (var index = 0; index < count; index++)
            for (var axis = 0; axis < 2; axis++)
                dual[index, axis] /= mass;

        // The SOCP dual requires Q^T * dualX = Q^T * dualY = 0 and
        // sum ||dual_i|| <= 1. Project away stationarity error, then enforce
        // the norm budget. This works even before an iterate is centered.
        for (var pass = 0; pass < 2; pass++)
            for (var axis = 0; axis < 2; axis++)
                for (var column = 0; column < 3; column++)
                {
                    var projection = 0d;
                    for (var index = 0; index < count; index++)
                        projection += basis[index, column] * dual[index, axis];
                    for (var index = 0; index < count; index++)
                        dual[index, axis] -= projection * basis[index, column];
                }
        mass = 0;
        var lowerBound = 0d;
        for (var index = 0; index < count; index++)
        {
            mass += Norm(dual[index, 0], dual[index, 1]);
            lowerBound += residuals[index, 0] * dual[index, 0] + residuals[index, 1] * dual[index, 1];
        }
        var stationaritySquared = 0d;
        for (var axis = 0; axis < 2; axis++)
            for (var column = 0; column < 3; column++)
            {
                var stationarity = 0d;
                for (var index = 0; index < count; index++)
                    stationarity += basis[index, column] * dual[index, axis];
                if (!double.IsFinite(stationarity) || Math.Abs(stationarity) > 1e-10)
                    throw Failure("对偶约束未达到数值精度");
                stationaritySquared += stationarity * stationarity;
            }
        // At an optimum, ||residual_i|| cannot exceed the initial maximum (<= 1
        // in these units). With sigma_min(Q) > 1/2, ||optimal correction||
        // <= 4*sqrt(count). Subtract its worst possible dot product with the
        // remaining stationarity defect instead of treating floating-point zero
        // as exact dual feasibility.
        lowerBound = (lowerBound - 4 * Math.Sqrt(count * stationaritySquared)) / Math.Max(1, mass);
        if (!double.IsFinite(lowerBound) || lowerBound > maximum + 1e-10)
            throw Failure("对偶下界无效");
        return Math.Max(0, lowerBound);
    }

    private static double[] ComposeMatrix(double[] variables, double[,] triangular, double[] initial,
        int sourceWidth, int sourceHeight, double residualScale)
    {
        var matrix = (double[])initial.Clone();
        for (var axis = 0; axis < 2; axis++)
        {
            var coefficients = new double[3];
            for (var row = 2; row >= 0; row--)
            {
                var value = variables[axis * 3 + row];
                for (var column = row + 1; column < 3; column++)
                    value -= triangular[row, column] * coefficients[column];
                coefficients[row] = value / triangular[row, row];
            }
            matrix[axis * 3] += residualScale * coefficients[0] / sourceWidth;
            matrix[axis * 3 + 1] += residualScale * coefficients[1] / sourceHeight;
            matrix[axis * 3 + 2] += residualScale * (coefficients[2] - 0.5 * coefficients[0] - 0.5 * coefficients[1]);
        }
        return matrix;
    }

    private static void Residual(double[,] basis, double[,] residuals, double[] variables,
        int index, out double x, out double y)
    {
        x = residuals[index, 0];
        y = residuals[index, 1];
        for (var column = 0; column < 3; column++)
        {
            x += basis[index, column] * variables[column];
            y += basis[index, column] * variables[column + 3];
        }
    }

    private static double MaximumResidual(double[] matrix, IReadOnlyList<MapArtworkLandmark> landmarks)
    {
        var maximum = 0d;
        foreach (var point in landmarks)
            maximum = Math.Max(maximum, Norm(
                matrix[0] * point.SourceX + matrix[1] * point.SourceY + matrix[2] - point.ReferenceX,
                matrix[3] * point.SourceX + matrix[4] * point.SourceY + matrix[5] - point.ReferenceY));
        return maximum;
    }

    private static double Norm(double x, double y)
    {
        var largest = Math.Max(Math.Abs(x), Math.Abs(y));
        if (largest == 0 || !double.IsFinite(largest))
            return largest;
        x /= largest;
        y /= largest;
        return largest * Math.Sqrt(x * x + y * y);
    }

    private static InvalidOperationException Failure(string reason) =>
        new($"无法完成最小最大误差配准：{reason}。请检查对应点。");
}
